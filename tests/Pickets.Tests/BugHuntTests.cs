using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Pickets.Services;
using Xunit;

namespace Pickets.Tests
{
    /// <summary>Regressions from the October 2026 bug hunt.</summary>
    public class BugHuntTests : IDisposable
    {
        private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PicketsTests-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        // ----- A drop never overwrites or takes over a desktop item -----
        [Fact]
        public void A_dropped_item_gets_a_free_name_when_the_desktop_already_has_one()
        {
            Assert.Equal(Path.Combine(_dir, "Notes.lnk"), FenceWindow.UniqueDesktopPath(_dir, "Notes.lnk"));

            File.WriteAllText(Path.Combine(_dir, "Notes.lnk"), "x");
            File.WriteAllText(Path.Combine(_dir, "Notes (2).lnk"), "x");
            Assert.Equal(Path.Combine(_dir, "Notes (3).lnk"), FenceWindow.UniqueDesktopPath(_dir, "Notes.lnk"));

            Directory.CreateDirectory(Path.Combine(_dir, "Report"));
            Assert.Equal(Path.Combine(_dir, "Report (2)"), FenceWindow.UniqueDesktopPath(_dir, "Report"));
        }

        // ----- Only what Explorer would show -----
        [Theory]
        [InlineData(FileAttributes.Normal, false, false, true)]
        [InlineData(FileAttributes.Hidden, false, false, false)]   // ~$Report.docx lock files
        [InlineData(FileAttributes.Hidden, true, false, true)]     // Show hidden items on
        [InlineData(FileAttributes.Hidden | FileAttributes.System, true, false, false)] // desktop.ini, Thumbs.db
        [InlineData(FileAttributes.Hidden | FileAttributes.System, true, true, true)]
        [InlineData(FileAttributes.System, false, false, true)]    // system but not hidden: Explorer shows it
        public void Desktop_items_follow_explorers_show_hidden_settings(FileAttributes attributes, bool showHidden, bool showSystem, bool shown)
        {
            var shows = new DesktopItems.ExplorerShows(showHidden, showSystem);
            Assert.Equal(shown, shows.Shows(@"C:\Users\a\Desktop\settings.ini", attributes));
            Assert.False(shows.Shows(@"C:\Users\a\Desktop\~WRD0000.tmp", FileAttributes.Normal));
        }

        // ----- A shortcut loop doesn't take the process down -----
        [Fact]
        public void Shortcuts_pointing_at_each_other_still_get_an_icon()
        {
            var a = Path.Combine(_dir, "a.lnk");
            var b = Path.Combine(_dir, "b.lnk");
            var real = Path.Combine(_dir, "real.txt");
            File.WriteAllText(real, "x");
            ImageResult? result = null;
            Exception? failed = null;
            var t = new Thread(() =>
            {
                try
                {
                    // Some Windows setups won't save a shortcut to something that doesn't exist yet:
                    // b → a real file, a → b, then b → a, so each target exists when it's saved.
                    ShellLink.CreateShortcut(b, real);
                    ShellLink.CreateShortcut(a, b);
                    ShellLink.CreateShortcut(b, a);
                    result = new ImageResult(IconHelper.GetImageSourceForPath(a) != null);
                }
                catch (Exception ex) { failed = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(30)), "Getting the icon didn't finish.");
            Assert.Null(failed);
            Assert.NotNull(result); // before the fix this recursed until the process died
        }

        private sealed record ImageResult(bool HasImage);

        // ----- Importing someone else's layout -----
        [Theory]
        [InlineData(@"\\server\share\a.png", true)]
        [InlineData(@"//server/share/a.png", true)]
        [InlineData(@"\\?\UNC\server\share\a.png", true)]
        [InlineData(@"\\?\C:\Users\a\Desktop\x.txt", false)]
        [InlineData(@"C:\Users\a\Desktop\x.txt", false)]
        [InlineData(@"shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false)]
        public void Network_paths_are_recognised(string path, bool network) =>
            Assert.Equal(network, LayoutSnapshots.IsNetworkPath(path));

        [Fact]
        public void An_imported_layout_loses_network_items_and_backgrounds_and_lists_network_portals()
        {
            var file = Path.Combine(_dir, "shared.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                Name = "Shared",
                Fences = new object[]
                {
                    new { Name = "Docs", ItemPaths = new[] { @"\\203.0.113.5\s\bait.txt", @"C:\Users\a\Desktop\ok.txt" }, BackgroundMedia = @"\\203.0.113.5\s\bg.png" },
                    new { Name = "Share", IsPortal = true, FolderPath = @"\\nas\photos" },
                    new { Name = "Local", IsPortal = true, FolderPath = @"C:\Users\a\Downloads" },
                },
            }));

            var snap = LayoutSnapshots.Import(file);

            var docs = snap.Fences.Single(f => f.Name == "Docs");
            Assert.Equal(new[] { @"C:\Users\a\Desktop\ok.txt" }, docs.ItemPaths);
            Assert.Null(docs.BackgroundMedia);
            Assert.Equal(new[] { @"\\nas\photos" }, snap.NetworkFolders);

            LayoutSnapshots.RemoveNetworkPortals(snap);
            Assert.Equal(new[] { "Docs", "Local" }, snap.Fences.Select(f => f.Name));
        }

        [Fact]
        public void An_imported_layout_with_missing_lists_and_huge_numbers_is_made_safe()
        {
            var file = Path.Combine(_dir, "odd.json");
            File.WriteAllText(file, """
            {
              "Name": "Odd",
              "Fences": [ { "Name": "A", "ItemPaths": null, "Tabs": [ { "Name": "T", "ItemPaths": null } ], "PortalMaxAgeDays": 2000000000 }, null ],
              "Rules": [ { "Kind": "OlderThan", "Amount": 1e300, "Extensions": null, "TargetFence": "Old" }, null ]
            }
            """);

            var snap = LayoutSnapshots.Import(file);

            var a = Assert.Single(snap.Fences);
            Assert.Empty(a.ItemPaths);
            Assert.Empty(a.Tabs[0].ItemPaths);
            Assert.True(a.PortalMaxAgeDays <= 36500);
            var rule = Assert.Single(snap.Rules!);
            Assert.NotNull(rule.Extensions);
            Assert.True(rule.Amount <= 36500);
            // The rule can be worked out without throwing (dates far in the past do throw).
            DesktopRules.ResolveTargetFence(Path.Combine(_dir, "shared.json"), snap.Rules!);
        }
    }
}
