using StickStation.Core;

namespace StickStation.Core.Tests;

public class FileInventoryTests
{
    [Theory]
    [InlineData("HIT A01", true)]
    [InlineData("HIT D40", true)]
    [InlineData("HIT B12", true)]
    [InlineData("HIT E01", false)]
    [InlineData("HIT A00", false)]
    [InlineData("HIT A41", false)]
    [InlineData("HIT-A01", false)]
    [InlineData("hit A01", false)]
    [InlineData("HIT A1", false)]
    [InlineData("HIT A01 ", false)]
    [InlineData(null, false)]
    public void VolumeLabelsAreValidated(string? name, bool expected) =>
        Assert.Equal(expected, VolumeLabel.IsValid(name));

    [Fact]
    public void CopyVerifiedProducesIdenticalTree()
    {
        using var box = new Sandbox();
        var source = box.Folder("src");
        Sandbox.Write(Path.Combine(source, "a.txt"), "hello");
        Sandbox.Write(Path.Combine(source, "sub", "b.bin"), "world");
        Directory.CreateDirectory(Path.Combine(source, "empty"));

        var target = Path.Combine(box.Root, "dst");
        FileInventory.CopyVerified(source, target);

        Assert.True(FileInventory.Equal(FileInventory.Build(source), FileInventory.Build(target)));
        Assert.Equal("world", File.ReadAllText(Path.Combine(target, "sub", "b.bin")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
    }

    [Fact]
    public void InventoryDetectsContentChanges()
    {
        using var box = new Sandbox();
        var file = Path.Combine(box.Root, "f.txt");
        Sandbox.Write(file, "one");
        var before = FileInventory.Build(file);
        File.WriteAllText(file, "two");
        Assert.False(FileInventory.Equal(before, FileInventory.Build(file)));
    }

    [Fact]
    public void JunctionsAreRejected()
    {
        using var box = new Sandbox();
        var source = box.Folder("src");
        Sandbox.MakeJunction(Path.Combine(source, "link"), box.Folder("elsewhere"));

        var error = Assert.Throws<StickException>(() => FileInventory.Build(source));
        Assert.Equal(StickErrorCode.SymbolicLink, error.Code);
    }

    [Fact]
    public void PathSafetyResolvesJunctionsAndNesting()
    {
        using var box = new Sandbox();
        var real = box.Folder("real");
        var link = Path.Combine(box.Root, "link");
        Sandbox.MakeJunction(link, real);

        Assert.Equal(real, PathSafety.Resolve(Path.Combine(link, "nested")).Replace("\\nested", ""));
        Assert.True(PathSafety.IsSameOrInside(@"C:\A\B\c", @"C:\a\b"));
        Assert.False(PathSafety.IsSameOrInside(@"C:\A\Bc", @"C:\A\B"));
    }
}
