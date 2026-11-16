import Foundation
import CryptoKit

enum StickError: LocalizedError {
    case message(LocalizedStringResource)
    var errorDescription: String? {
        switch self { case .message(let text): return String(localized: text) }
    }

    // Keep app-authored diagnostic messages in English regardless of the UI language.
    // Details supplied by macOS retain the language chosen by the operating system.
    var diagnosticDescription: String {
        switch self {
        case .message(var text):
            text.locale = Locale(identifier: "en")
            return String(localized: text)
        }
    }
}

struct StickVolume: Identifiable, Sendable {
    let id: String
    let name: String
    let url: URL
    let capacity: Int64
}

enum StickPhase: Sendable {
    case todo, working, saved, reset, ejected, failed

    var title: LocalizedStringResource {
        switch self {
        case .todo: "To do"
        case .working: "In process"
        case .saved: "Saved"
        case .reset: "Reset"
        case .ejected: "Ejected"
        case .failed: "Needs attention"
        }
    }
}

struct StickRow: Identifiable {
    let volume: StickVolume
    var id: String { volume.id }
    var selected = true
    var phase: StickPhase = .todo
    var detail: LocalizedStringResource = "Ready to back up"
    var backup: URL?
    var isConnected = true
}

/// Serializes appends from all concurrent stick workers.
actor OperationLog {
    static let shared = OperationLog()

    func append(in directory: URL, stick: String, message: String) throws {
        let url = directory.appendingPathComponent("StickStation.log")
        let cleanMessage = message.replacingOccurrences(of: "\n", with: " | ")
        let line = "\(ISO8601DateFormatter().string(from: Date())) [\(stick)] \(cleanMessage)\n"
        if !FileManager.default.fileExists(atPath: url.path) {
            guard FileManager.default.createFile(atPath: url.path, contents: nil) else {
                throw StickError.message("Cannot create the operation log in the backup directory.")
            }
        }
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(line.utf8))
        try handle.synchronize()
    }
}

/// One worker per stick allows independent I/O without blocking the interface.
actor StickWorker {
    private let fm = FileManager.default

    static func validLabel(_ name: String) -> Bool {
        name.range(of: #"^HIT [ABCD](0[1-9]|[1-3][0-9]|40)$"#, options: .regularExpression) != nil
    }

    static func discover() -> [StickVolume] {
        let keys: Set<URLResourceKey> = [.volumeNameKey, .volumeUUIDStringKey,
            .volumeIsInternalKey, .volumeIsLocalKey, .volumeTotalCapacityKey]
        return (FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: Array(keys)) ?? [])
            .compactMap { url in
                guard let v = try? url.resourceValues(forKeys: keys),
                      v.volumeIsInternal == false, v.volumeIsLocal == true,
                      let name = v.volumeName, validLabel(name),
                      let uuid = v.volumeUUIDString else { return nil }
                return StickVolume(id: uuid, name: name, url: url, capacity: Int64(v.volumeTotalCapacity ?? 0))
            }.sorted { $0.name < $1.name }
    }

    private func command(_ arguments: [String]) throws -> Data {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/diskutil")
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let diagnostic = String(data: data, encoding: .utf8) ?? ""
            throw StickError.message("Disk operation failed: \(diagnostic)")
        }
        return data
    }

    private func info(_ path: String) throws -> [String: Any] {
        let data = try command(["info", "-plist", path])
        guard let result = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else {
            throw StickError.message("Cannot identify this USB volume.")
        }
        return result
    }

    private func validate(_ volume: StickVolume) throws -> String {
        let data = try info(volume.url.path)
        guard data["Internal"] as? Bool == false,
              data["BusProtocol"] as? String == "USB",
              data["VolumeName"] as? String == volume.name,
              (data["VolumeUUID"] as? String)?.uppercased() == volume.id.uppercased(),
              data["MountPoint"] as? String == volume.url.path,
              let device = data["DeviceIdentifier"] as? String,
              device.range(of: #"^disk[0-9]+s[0-9]+$"#, options: .regularExpression) != nil else {
            throw StickError.message("USB identity changed or this is not a supported USB partition. Reconnect and refresh.")
        }
        return device
    }

    /// A failed unmount callback can arrive after the whole disk has disappeared.
    /// Query the disk list rather than treating an inaccessible mount path as success.
    private func diskIsPresent(_ wholeDisk: String) throws -> Bool {
        let data = try command(["list", "-plist"])
        guard let list = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let disks = list["AllDisks"] as? [String] else {
            throw StickError.message("Cannot verify whether the USB disk was ejected.")
        }
        return disks.contains(wholeDisk)
    }

    private func eject(_ volume: StickVolume, device: String) async throws {
        let identity = try info(device)
        guard let wholeDisk = identity["ParentWholeDisk"] as? String else {
            throw StickError.message("Cannot identify the USB disk for ejection.")
        }
        for attempt in 0..<3 {
            guard try validate(volume) == device else {
                throw StickError.message("The USB device changed before ejection.")
            }
            do {
                try await fm.unmountVolume(at: volume.url, options: [.allPartitionsAndEjectDisk])
                return
            } catch {
                let ejectionError = error
                // Allow Disk Arbitration to finish updating after a format/eject.
                for _ in 0..<3 {
                    try await Task.sleep(for: .milliseconds(500))
                    if try !diskIsPresent(wholeDisk) { return }
                }
                guard attempt < 2 else { throw ejectionError }
                // Only retry while the same volume is still mounted.
                guard (try? validate(volume)) == device else { throw ejectionError }
            }
        }
    }

    private func children(_ url: URL) throws -> [URL] {
        try fm.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)
    }

    private func isPermissionError(_ error: Error) -> Bool {
        let error = error as NSError
        if error.domain == NSCocoaErrorDomain && error.code == NSFileWriteNoPermissionError {
            return true
        }
        if error.domain == NSPOSIXErrorDomain && (error.code == 1 || error.code == 13) {
            return true
        }
        if let underlying = error.userInfo[NSUnderlyingErrorKey] as? NSError {
            return isPermissionError(underlying)
        }
        return false
    }

    /// Request macOS authorization only after ordinary removal fails.
    private func removeWithAdministratorPermission(_ item: URL, from volume: StickVolume) throws {
        _ = try validate(volume)
        guard item.deletingLastPathComponent().standardizedFileURL == volume.url.standardizedFileURL else {
            throw StickError.message("Cleanup target is outside the USB root.")
        }
        // Pass paths as arguments, never as AppleScript source. Pin the working directory
        // and check the volume UUID again after the authorization dialog has closed.
        let script = """
        on run arguments
            set rootPath to item 1 of arguments
            set itemName to item 2 of arguments
            set expectedUUID to item 3 of arguments
            set cleanupCommand to "cd " & quoted form of rootPath & " && actualUUID=$(/usr/sbin/diskutil info -plist . | /usr/bin/plutil -extract VolumeUUID raw -o - -) && /bin/test " & quoted form of expectedUUID & " = \\"$actualUUID\\" && /bin/rm -rf -- " & quoted form of ("./" & itemName)
            do shell script cleanupCommand with administrator privileges with prompt (item 4 of arguments)
        end run
        """
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        let prompt = String(localized: "StickStation needs permission to remove protected files from \(volume.url.path).")
        process.arguments = ["-e", script, volume.url.path, item.lastPathComponent, volume.id, prompt]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let output = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else {
            let outputText = String(decoding: output, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            let detail = outputText.isEmpty ? String(localized: "No diagnostic output.") : outputText
            throw StickError.message("Cleanup was not completed (exit \(process.terminationStatus)). Administrator permission was cancelled or removal failed: \(detail)")
        }
    }

    private func digest(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hash = SHA256()
        while let chunk = try handle.read(upToCount: 1_048_576), !chunk.isEmpty {
            hash.update(data: chunk)
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// Reject links and special files so a backup never escapes its source tree.
    private func inventory(_ url: URL, prefix: String = "") throws -> [String: String] {
        let values = try url.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey, .isRegularFileKey])
        guard values.isSymbolicLink != true else {
            throw StickError.message("Symbolic links are not supported: \(url.lastPathComponent). Stick was not cleared.")
        }
        if values.isDirectory == true {
            var result = [prefix: "directory"]
            for child in try children(url) {
                result.merge(try inventory(child, prefix: prefix + "/" + child.lastPathComponent)) { first, _ in first }
            }
            return result
        }
        guard values.isRegularFile == true else {
            throw StickError.message("Unsupported file: \(url.lastPathComponent)")
        }
        return [prefix: try digest(url)]
    }

    private func copyVerified(_ source: URL, _ target: URL) throws {
        let before = try inventory(source)
        try fm.copyItem(at: source, to: target)
        guard try inventory(target) == before, try inventory(source) == before else {
            throw StickError.message("Verification failed for \(source.lastPathComponent). Stick was not cleared.")
        }
    }

    func process(_ volume: StickVolume, backupRoot: URL, source: URL?, format: Bool,
                 update: @escaping @Sendable (StickPhase, LocalizedStringResource, URL?) async -> Void) async throws {
        do {
            try await processAndEject(volume, backupRoot: backupRoot, source: source, format: format, update: update)
        } catch {
            let operationError = error
            do {
                try await OperationLog.shared.append(in: backupRoot, stick: volume.name,
                                                     message: "ERROR: \((operationError as? StickError)?.diagnosticDescription ?? operationError.localizedDescription)")
            } catch {
                throw StickError.message("\(operationError.localizedDescription) Log could not be written: \(error.localizedDescription)")
            }
            throw operationError
        }
    }

    private func processAndEject(_ volume: StickVolume, backupRoot: URL, source: URL?, format: Bool,
                                update: @escaping @Sendable (StickPhase, LocalizedStringResource, URL?) async -> Void) async throws {
        let log: (String) async throws -> Void = { message in
            try await OperationLog.shared.append(in: backupRoot, stick: volume.name, message: message)
        }
        let device = try validate(volume)
        // Reject either selected folder on any managed stick, including other batch members.
        for folder in [backupRoot, source].compactMap({ $0 }) {
            let resolved = folder.resolvingSymlinksInPath()
            let values = try resolved.resourceValues(forKeys: [.volumeNameKey])
            guard !Self.validLabel(values.volumeName ?? "") else {
                throw StickError.message("Backup and source folders must not be on a HIT stick.")
            }
        }
        if let source {
            let sourcePath = source.resolvingSymlinksInPath().path
            let backupPath = backupRoot.resolvingSymlinksInPath().path
            guard backupPath != sourcePath, !backupPath.hasPrefix(sourcePath + "/") else {
                throw StickError.message("The backup directory cannot be inside the assignment source.")
            }
        }
        try await log("Started. UUID: \(volume.id); mode: \(format ? "FAT32 format" : "clear files"); assignment: \(source?.path ?? "none")")
        let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
        let destination = backupRoot.appendingPathComponent("\(volume.name)_\(stamp)_\(UUID().uuidString.prefix(8))")
        try fm.createDirectory(at: destination, withIntermediateDirectories: false)
        try await log("Backup directory created: \(destination.path)")
        let payload = destination.appendingPathComponent("Files")
        try fm.createDirectory(at: payload, withIntermediateDirectories: false)
        let rootItems = try children(volume.url)
        if !rootItems.contains(where: { $0.lastPathComponent == "Abgabe" }) {
            try await log("No Abgabe folder present; continuing with backup and reset")
        }
        var snapshots: [URL: [String: String]] = [:]
        for item in rootItems {
            let values = try item.resourceValues(forKeys: [.isDirectoryKey])
            if item.lastPathComponent == "Abgabe" || values.isDirectory != true {
                snapshots[item] = try inventory(item)
                try copyVerified(item, payload.appendingPathComponent(item.lastPathComponent))
                try await log("Copied and verified: \(item.lastPathComponent)")
            }
        }
        // Stage the optional assignment before deleting anything from the USB stick.
        let staged = destination.appendingPathComponent("Assignment")
        if let source {
            try copyVerified(source, staged)
            try await log("Assignment source staged and verified")
        }
        let receipt = "Verified backup\nVolume: \(volume.name)\nUUID: \(volume.id)\nDate: \(stamp)\nIncludes Abgabe when present and all root files.\n"
        try receipt.write(to: destination.appendingPathComponent("Backup.txt"), atomically: true, encoding: .utf8)
        for (item, snapshot) in snapshots {
            guard try inventory(item) == snapshot,
                  try inventory(payload.appendingPathComponent(item.lastPathComponent)) == snapshot else {
                throw StickError.message("Files changed during backup. No data was deleted.")
            }
        }
        guard Set(try children(volume.url)) == Set(rootItems) else {
            throw StickError.message("Root directory changed during backup. No data was deleted.")
        }
        try await log("Backup verification completed")
        await update(.saved, "Backup verified", destination)
        guard try validate(volume) == device else { throw StickError.message("The USB device changed.") }
        await update(.saved, format ? "Formatting as FAT32…" : "Clearing stick…", destination)
        var root = volume.url
        var currentVolume = volume
        if format {
            try await log("Starting FAT32 format")
            _ = try command(["eraseVolume", "MS-DOS FAT32", volume.name, device])
            let refreshed = try info(device)
            guard refreshed["VolumeName"] as? String == volume.name,
                  refreshed["Internal"] as? Bool == false,
                  let uuid = refreshed["VolumeUUID"] as? String,
                  let mount = refreshed["MountPoint"] as? String else {
                throw StickError.message("Formatted, but the volume did not remount. Backup is safe.")
            }
            root = URL(fileURLWithPath: mount)
            currentVolume = StickVolume(id: uuid, name: volume.name, url: root, capacity: volume.capacity)
            try await log("FAT32 format completed")
        } else {
            try await log("Starting removal of all root items")
            var retainedSpotlight = false
            for item in try children(root) {
                _ = try validate(volume)
                do {
                    try fm.removeItem(at: item)
                } catch {
                    guard isPermissionError(error) else { throw error }
                    if item.lastPathComponent == ".Spotlight-V100" {
                        retainedSpotlight = true
                        try await log("Skipped protected .Spotlight-V100: \(error.localizedDescription)")
                        continue
                    }
                    try await log("Requesting administrator permission to delete: \(item.lastPathComponent)")
                    await update(.saved, "Administrator permission required…", destination)
                    try removeWithAdministratorPermission(item, from: volume)
                    await update(.saved, "Clearing stick…", destination)
                }
                try await log("Deleted: \(item.lastPathComponent)")
            }
            let remaining = try children(root).filter {
                !(retainedSpotlight && $0.lastPathComponent == ".Spotlight-V100")
            }
            guard remaining.isEmpty else {
                throw StickError.message("Cleanup is incomplete: items remain on the stick. macOS may have recreated system files.")
            }
        }
        try fm.createDirectory(at: root.appendingPathComponent("Abgabe"), withIntermediateDirectories: false)
        try await log("Created empty Abgabe folder")
        let assignment = root.appendingPathComponent("Angabe")
        if source != nil {
            try copyVerified(staged, assignment)
            try await log("Created Angabe with verified assignment contents")
        } else {
            try fm.createDirectory(at: assignment, withIntermediateDirectories: false)
            try await log("Created empty Angabe folder")
        }
        try "Reset completed: \(Date())\n".write(to: destination.appendingPathComponent("Reset.txt"), atomically: true, encoding: .utf8)
        try await log("Reset completed; starting automatic ejection")
        await update(.reset, "Reset complete • ejecting…", destination)
        guard try validate(currentVolume) == device else {
            throw StickError.message("Reset complete, but device identity changed before ejection.")
        }
        do {
            try await eject(currentVolume, device: device)
        } catch {
            throw StickError.message("Backup and reset completed, but ejection failed: \(error.localizedDescription)")
        }
        await update(.ejected, "Safe to remove", destination)
        do {
            try await log("Ejected successfully; safe to remove")
        } catch {
            throw StickError.message("Stick ejected successfully, but the final log entry failed: \(error.localizedDescription)")
        }
    }
}
