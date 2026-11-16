import SwiftUI
import AppKit
import Observation

@MainActor @Observable
final class StickController {
    var sticks: [StickRow] = []
    var backupRoot: URL?
    var source: URL?
    var format = false
    var running = false
    var message: String?
    var completed = 0
    private var activeIDs: Set<String> = []
    private var refreshGeneration = 0

    var selectedCount: Int { sticks.filter { $0.selected && $0.phase == .todo }.count }

    func chooseFolder(assignment: Bool) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = !assignment
        panel.allowsMultipleSelection = false
        panel.prompt = String(localized: "Choose folder")
        panel.message = assignment ? String(localized: "Choose the contents to copy into Angabe.") : String(localized: "Choose where verified backups will be stored.")
        if panel.runModal() == .OK, let url = panel.url {
            if assignment { source = url } else { backupRoot = url }
        }
    }

    func refresh() async {
        refreshGeneration += 1
        let generation = refreshGeneration
        let volumes = await Task.detached { StickWorker.discover() }.value
        guard generation == refreshGeneration else { return }
        reconcile(volumes)
    }

    // Keep processing rows stable while formatting temporarily changes a volume's UUID.
    func reconcile(_ volumes: [StickVolume]) {
        var remaining = volumes
        var updated: [StickRow] = []
        for var row in sticks {
            if activeIDs.contains(row.id) {
                remaining.removeAll { $0.id == row.id || $0.url == row.volume.url }
                updated.append(row)
            } else if let index = remaining.firstIndex(where: { $0.id == row.id }) {
                let volume = remaining.remove(at: index)
                if !row.isConnected {
                    updated.append(StickRow(volume: volume))
                } else {
                    row.isConnected = true
                    updated.append(row)
                }
            } else if row.phase != .todo {
                row.isConnected = false
                updated.append(row)
            }
        }
        updated.append(contentsOf: remaining.map { StickRow(volume: $0) })
        sticks = updated.sorted { $0.volume.name < $1.volume.name }
    }

    func observeVolumes(named name: Notification.Name) async {
        let notifications = NSWorkspace.shared.notificationCenter.notifications(named: name)
        for await _ in notifications {
            guard !Task.isCancelled else { return }
            await refresh()
        }
    }

    func start() {
        guard !running, let backupRoot else { return }
        let batch = Array(sticks.filter { $0.selected && $0.phase == .todo }.prefix(16))
        guard !batch.isEmpty else { return }
        activeIDs = Set(batch.map(\.id))
        running = true
        completed = 0
        let source = source
        let format = format
        for row in batch { set(row.id, phase: .working, detail: "Copying and verifying…", backup: nil) }
        Task {
            await withTaskGroup(of: Void.self) { group in
                for row in batch {
                    group.addTask {
                        do {
                            try await StickWorker().process(row.volume, backupRoot: backupRoot, source: source, format: format) { phase, detail, backup in
                                await self.set(row.id, phase: phase, detail: detail, backup: backup)
                            }
                        } catch {
                            await self.set(row.id, phase: .failed, detail: "\(error.localizedDescription)", backup: nil)
                        }
                        await self.finishedOne()
                    }
                }
            }
            activeIDs.removeAll()
            running = false
            await refresh()
        }
    }

    private func finishedOne() { completed += 1 }

    private func set(_ id: String, phase: StickPhase, detail: LocalizedStringResource, backup: URL?) {
        guard let index = sticks.firstIndex(where: { $0.id == id }) else { return }
        sticks[index].phase = phase
        if phase == .ejected { sticks[index].isConnected = false }
        sticks[index].detail = detail
        if let backup { sticks[index].backup = backup }
    }


}

private let accent = Color(red: 0.20, green: 0.35, blue: 0.89)

struct ContentView: View {
    @State private var model = StickController()
    @State private var confirm = false

    var body: some View {
        HStack(spacing: 0) {
            SetupSidebar(model: model)
                .frame(width: 280)
            Divider()
            VStack(alignment: .leading, spacing: 24) {
                DashboardHeader(count: model.sticks.count, running: model.running)
                HStack(spacing: 12) {
                    SummaryTile(title: "To do", count: model.sticks.filter { $0.phase == .todo }.count, icon: "circle.dashed", color: .secondary)
                    SummaryTile(title: "In process", count: model.sticks.filter { $0.phase == .working || $0.phase == .saved }.count, icon: "arrow.triangle.2.circlepath", color: accent)
                    SummaryTile(title: "Reset", count: model.sticks.filter { $0.phase == .reset || $0.phase == .ejected }.count, icon: "checkmark.circle.fill", color: .green)
                }
                HStack {
                    Text("Connected sticks & recent results").font(.title3.weight(.semibold))
                    Spacer()
                    Button("Select all") {
                        for index in model.sticks.indices where model.sticks[index].phase == .todo {
                            model.sticks[index].selected = true
                        }
                    }.buttonStyle(.plain).foregroundStyle(accent)
                    Button { Task { await model.refresh() } } label: { Image(systemName: "arrow.clockwise") }
                        .help("Refresh connected volumes")
                        .accessibilityLabel("Refresh connected volumes")
                }.disabled(model.running)
                if model.sticks.isEmpty {
                    ContentUnavailableView {
                        Label("Ready for your USB sticks", systemImage: "externaldrive.badge.plus")
                    } description: {
                        Text("Connect your USB hub. Sticks labeled HIT A01 through HIT D40 will appear here automatically.")
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    ScrollView {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 240), spacing: 16)], spacing: 16) {
                            ForEach($model.sticks) { $row in
                                StickCard(row: $row, running: model.running)
                            }
                        }.padding(2)
                    }
                }
                BatchFooter(model: model, confirm: $confirm)
            }
            .padding(28)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .frame(minWidth: 960, minHeight: 680)
        .tint(accent)
        .task {
            while !Task.isCancelled {
                await model.refresh()
                try? await Task.sleep(for: .seconds(3))
            }
        }
        .task { await model.observeVolumes(named: NSWorkspace.didMountNotification) }
        .task { await model.observeVolumes(named: NSWorkspace.didUnmountNotification) }
        .task { await model.observeVolumes(named: NSWorkspace.didRenameVolumeNotification) }
        .confirmationDialog("Back up and reset \(model.selectedCount) sticks?", isPresented: $confirm, titleVisibility: .visible) {
            Button(role: .destructive) { model.start() } label: {
                Text(model.format ? LocalizedStringResource("Back up & format") : LocalizedStringResource("Back up & reset"))
            }
        } message: {
            if model.format {
                Text("Abgabe and all root files will be copied and verified. Then ALL data on the selected sticks will be deleted by formatting as FAT32. Angabe and Abgabe will be recreated. Other folders are not backed up. Sticks will be ejected automatically after reset. Keep them connected until “Safe to remove” appears.")
            } else {
                Text("Abgabe and all root files will be copied and verified. Then ALL data on the selected sticks will be deleted. Angabe and Abgabe will be recreated. Other folders are not backed up. Sticks will be ejected automatically after reset. Keep them connected until “Safe to remove” appears.")
            }
        }
        .alert("USB operation", isPresented: Binding(get: { model.message != nil }, set: { if !$0 { model.message = nil } })) {
            Button("OK") { model.message = nil }
        } message: { Text(model.message ?? "") }
    }
}

private struct SetupSidebar: View {
    @Bindable var model: StickController

    var body: some View {
        VStack(alignment: .leading, spacing: 28) {
            HStack(spacing: 12) {
                Image(systemName: "externaldrive.fill")
                    .font(.title2).foregroundStyle(.white)
                    .frame(width: 42, height: 42)
                    .background(accent, in: RoundedRectangle(cornerRadius: 12))
                VStack(alignment: .leading, spacing: 2) {
                    Text(verbatim: "StickStation").font(.title3.bold())
                    Text("BACK UP. RESET. REPEAT.").font(.system(size: 9, weight: .semibold)).foregroundStyle(.secondary)
                }
            }
            Label("USB workspace", systemImage: "square.grid.2x2")
                .font(.headline).foregroundStyle(accent)
                .padding(12).frame(maxWidth: .infinity, alignment: .leading)
                .background(accent.opacity(0.09), in: RoundedRectangle(cornerRadius: 9))
            VStack(alignment: .leading, spacing: 20) {
                Text("BATCH SETUP").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                FolderSetting(title: "Backup directory", subtitle: "Required · one folder per stick", url: model.backupRoot, icon: "archivebox", action: { model.chooseFolder(assignment: false) })
                FolderSetting(title: "Assignment source", subtitle: "Optional · copied into Angabe", url: model.source, icon: "folder.badge.plus", action: { model.chooseFolder(assignment: true) })
                if model.source != nil {
                    Button("Clear assignment source") { model.source = nil }.font(.caption)
                }
                Divider()
                Toggle("Format as FAT32", isOn: $model.format).toggleStyle(.switch)
                Text("Off: clear files and keep the existing format. On: reformat the USB volume.")
                    .font(.caption).foregroundStyle(.secondary)
            }.disabled(model.running)
            Spacer()
            VStack(alignment: .leading, spacing: 10) {
                Label("Verified before reset", systemImage: "checkmark.shield").font(.subheadline.weight(.semibold))
                Text("Abgabe + all root files are saved and checked before a stick is cleared. Operations are recorded in StickStation.log in your backup directory.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(14).background(.background.opacity(0.65), in: RoundedRectangle(cornerRadius: 12))
            Text("Up to 16 sticks · automatic ejection").font(.caption).foregroundStyle(.secondary)
        }
        .padding(22)
        .background(Color(nsColor: .controlBackgroundColor))
    }
}

private struct FolderSetting: View {
    let title: LocalizedStringResource
    let subtitle: LocalizedStringResource
    let url: URL?
    let icon: String
    let action: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label {
                Text(title)
            } icon: {
                Image(systemName: icon)
            }
            .font(.subheadline.weight(.semibold))
            Text(subtitle).font(.caption).foregroundStyle(.secondary)
            Button(action: action) {
                HStack {
                    Group {
                        if let url {
                            Text(url.lastPathComponent)
                        } else {
                            Text("Choose folder…")
                        }
                    }.lineLimit(1)
                    Spacer()
                    Image(systemName: "folder")
                }.padding(10)
            }
            .buttonStyle(.plain)
            .background(.background, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(.quaternary))
            .help(url.map { Text($0.path) } ?? Text(title))
        }
    }
}

private struct DashboardHeader: View {
    let count: Int
    let running: Bool
    var body: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 6) {
                Text("A fresh start for every stick.").font(.system(size: 28, weight: .bold))
                Text("Save submissions. Prepare the next assignment.").foregroundStyle(.secondary)
            }
            Spacer()
            Label {
                Text(running ? LocalizedStringResource("Processing") : LocalizedStringResource("Monitoring USB"))
            } icon: {
                Image(systemName: running ? "arrow.triangle.2.circlepath" : "circle.fill")
            }
            .font(.caption.weight(.medium)).foregroundStyle(running ? accent : .green)
            .padding(9).background(.background, in: Capsule())
        }
    }
}

private struct SummaryTile: View {
    let title: LocalizedStringResource
    let count: Int
    let icon: String
    let color: Color
    var body: some View {
        HStack {
            Image(systemName: icon).font(.title2).foregroundStyle(color)
            Text(title).foregroundStyle(.secondary)
            Spacer()
            Text(count, format: .number).font(.title2.bold()).monospacedDigit()
        }
        .padding(18).frame(maxWidth: .infinity)
        .background(.background, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(.quaternary))
    }
}

private struct StickCard: View {
    @Binding var row: StickRow
    let running: Bool

    var color: Color {
        switch row.phase {
        case .todo: return .secondary
        case .working, .saved: return accent
        case .reset, .ejected: return .green
        case .failed: return .orange
        }
    }
    var icon: String {
        switch row.phase {
        case .todo: return "circle.dashed"
        case .working: return "arrow.triangle.2.circlepath"
        case .saved: return "checkmark.shield.fill"
        case .reset: return "checkmark.circle.fill"
        case .ejected: return "eject.circle.fill"
        case .failed: return "exclamationmark.triangle.fill"
        }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Image(systemName: "externaldrive.fill").font(.system(size: 26)).foregroundStyle(color)
                Spacer()
                if row.phase == .todo {
                    Toggle("Select \(row.volume.name)", isOn: $row.selected).labelsHidden()
                        .toggleStyle(.checkbox).disabled(running)
                } else { Image(systemName: icon).foregroundStyle(color) }
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(row.volume.name).font(.title3.bold()).monospaced()
                Text(ByteCountFormatter.string(fromByteCount: row.volume.capacity, countStyle: .file) + " · USB")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Divider()
            Label {
                Text(row.phase.title)
            } icon: {
                Image(systemName: icon)
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(color)
            Text(row.detail).font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, minHeight: 30, alignment: .topLeading)
                .textSelection(.enabled)
            if let backup = row.backup {
                Button("Show backup") { NSWorkspace.shared.open(backup) }.font(.caption)
            }
            if !row.isConnected && row.phase != .ejected {
                Label("Disconnected", systemImage: "externaldrive.badge.xmark")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(18)
        .background(.background, in: RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(row.selected && row.phase == .todo ? accent.opacity(0.45) : Color.secondary.opacity(0.18)))
    }
}

#Preview("German") {
    ContentView()
        .environment(\.locale, Locale(identifier: "de"))
}

private struct BatchFooter: View {
    @Bindable var model: StickController
    @Binding var confirm: Bool
    var body: some View {
        HStack(spacing: 14) {
            if model.running {
                ProgressView().controlSize(.small)
                Text("\(model.completed) finished · keep sticks connected").font(.subheadline)
            } else {
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(model.selectedCount) sticks selected").font(.subheadline.weight(.semibold))
                    Text(model.backupRoot == nil ? LocalizedStringResource("Choose a backup directory to begin.") : LocalizedStringResource("Backups are verified before any data is deleted."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer()
            Button {
                confirm = true
            } label: {
                Label("Back up & reset", systemImage: "arrow.right")
                    .padding(.horizontal, 12).padding(.vertical, 8)
            }
            .buttonStyle(.borderedProminent)
            .disabled(model.running || model.backupRoot == nil || model.selectedCount == 0 || model.selectedCount > 16)
        }
        .padding(18).background(.background, in: RoundedRectangle(cornerRadius: 12))
    }
}
