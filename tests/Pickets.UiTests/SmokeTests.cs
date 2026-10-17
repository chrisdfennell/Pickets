using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using Xunit;

namespace Pickets.UiTests
{
    /// <summary>
    /// Starts the real Pickets.exe in a sandbox (its own settings and desktop folder, the real
    /// desktop left alone; see Services/Sandbox) and checks it through UI Automation, the way a
    /// screen reader sees it: fences show the right items, every Settings switch has a name, and
    /// closing the window quits cleanly with the fences saved.
    /// </summary>
    public class SmokeTests
    {
        [Fact]
        public void The_app_starts_shows_its_fences_and_quits_cleanly()
        {
            var sandbox = Ui.TempFolder();
            var desktop = Path.Combine(sandbox, "Desktop");
            var data = Path.Combine(sandbox, "AppData");
            Directory.CreateDirectory(desktop);
            Directory.CreateDirectory(data);
            var notes = Path.Combine(desktop, "Notes.txt");
            File.WriteAllText(notes, "x");
            File.WriteAllText(Path.Combine(desktop, "Budget.xlsx"), "x");
            // An existing config, so there's no first-run welcome. Budget.xlsx isn't in a fence:
            // Pickets should put it in the Desktop fence.
            File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(new
            {
                Fences = new[] { new { Name = "Work", ItemPaths = new[] { notes }, Left = 120, Top = 120, Width = 400, Height = 240 } },
                Options = new { CheckForUpdates = false },
            }));

            var start = new ProcessStartInfo(PicketsExe()) { UseShellExecute = false };
            start.Environment["PICKETS_SANDBOX"] = sandbox;
            using var app = Process.Start(start)!;
            try
            {
                var work = WaitForWindow(app.Id, "Work fence");
                var desktopFence = WaitForWindow(app.Id, "Desktop fence");
                var main = WaitForWindow(app.Id, "Pickets");

                Assert.Equal(new[] { "Notes.txt" }, TileNames(work, "Work"));
                Assert.Contains("Budget.xlsx", TileNames(desktopFence, "Desktop"));

                // Settings: every switch is named after its row.
                var settings = main.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.NameProperty, "Settings")));
                Assert.NotNull(settings);
                ((SelectionItemPattern)settings.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                var toggles = Retry(() =>
                {
                    var found = main.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)).Cast<AutomationElement>().ToList();
                    return found.Count > 5 ? found : null;
                });
                Assert.All(toggles, t => Assert.False(string.IsNullOrWhiteSpace(t.Current.Name), "A Settings switch has no name."));
                Assert.Contains(toggles, t => t.Current.Name == "Keep stacked fences together");

                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
                Assert.True(app.WaitForExit(20000), "Pickets didn't quit after its window was closed.");
                Assert.Equal(0, app.ExitCode);

                var saved = File.ReadAllText(Path.Combine(data, "config.json"));
                Assert.Contains("\"Work\"", saved);
                Assert.Contains("Budget.xlsx", saved);
            }
            finally
            {
                if (!app.HasExited) app.Kill();
                Ui.DeleteTempFolders();
            }
        }

        [Fact]
        public void Saving_a_document_the_way_Word_does_keeps_it_in_its_fence()
        {
            var (sandbox, desktop, data) = NewSandbox();
            var report = Path.Combine(desktop, "Report.docx");
            File.WriteAllText(report, "v1");
            File.WriteAllText(Path.Combine(data, "config.json"), JsonSerializer.Serialize(new
            {
                Fences = new[] { new { Name = "Work", ItemPaths = new[] { report }, Left = 120, Top = 120, Width = 400, Height = 240 } },
                Options = new { CheckForUpdates = false, DesktopTakenOver = true },
            }));

            using var app = Start(sandbox);
            try
            {
                var work = WaitForWindow(app.Id, "Work fence");
                Assert.Equal(new[] { "Report.docx" }, TileNames(work, "Work"));

                // Word's save: the original renamed away, the new version renamed into its place,
                // then the old copy deleted.
                var newVersion = Path.Combine(desktop, "~WRD0000.tmp");
                var oldCopy = Path.Combine(desktop, "~WRL0001.tmp");
                File.WriteAllText(newVersion, "v2");
                File.Move(report, oldCopy);
                File.Move(newVersion, report);
                File.Delete(oldCopy);
                Thread.Sleep(3500); // past the 2-second settle

                Assert.Equal(new[] { "Report.docx" }, TileNames(work, "Work"));
                Assert.Null(FindWindow(app.Id, "Desktop fence")); // nothing else on this desktop to show

                ((WindowPattern)WaitForWindow(app.Id, "Pickets").GetCurrentPattern(WindowPattern.Pattern)).Close();
                Assert.True(app.WaitForExit(20000));
                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "config.json")));
                var fences = saved.RootElement.GetProperty("Fences").EnumerateArray().ToList();
                var items = fences.Single(f => f.GetProperty("Name").GetString() == "Work").GetProperty("ItemPaths")
                                  .EnumerateArray().Select(p => p.GetString()).ToList();
                Assert.Equal(new[] { report }, items);
            }
            finally
            {
                if (!app.HasExited) app.Kill();
                Ui.DeleteTempFolders();
            }
        }

        [Fact]
        public void A_first_start_leaves_the_desktop_alone_until_the_user_says_so()
        {
            var (sandbox, desktop, data) = NewSandbox();
            File.WriteAllText(Path.Combine(desktop, "Notes.txt"), "x");
            File.WriteAllText(Path.Combine(desktop, "Budget.xlsx"), "x");
            // No config: a first start, with the welcome.

            using var app = Start(sandbox);
            try
            {
                var welcome = Retry(() => FindWindow(app.Id, "Welcome to Pickets"), timeoutMs: 30000);
                Thread.Sleep(1500); // time for anything that would take over to happen
                Assert.Null(FindWindow(app.Id, "Desktop fence"));

                Click(welcome, "I'll set it up myself");
                var main = WaitForWindow(app.Id, "Pickets");
                ((WindowPattern)main.GetCurrentPattern(WindowPattern.Pattern)).Close();
                Assert.True(app.WaitForExit(20000), "Pickets didn't quit after its window was closed.");

                using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "config.json")));
                Assert.Equal(0, saved.RootElement.GetProperty("Fences").GetArrayLength());
                Assert.False(saved.RootElement.GetProperty("Options").GetProperty("DesktopTakenOver").GetBoolean());
            }
            finally
            {
                if (!app.HasExited) app.Kill();
                Ui.DeleteTempFolders();
            }
        }

        [Fact]
        public void Organizing_from_the_welcome_sorts_every_item_without_a_Desktop_fence()
        {
            var (sandbox, desktop, _) = NewSandbox();
            File.WriteAllText(Path.Combine(desktop, "Notes.txt"), "x");
            File.WriteAllText(Path.Combine(desktop, "Setup.exe"), "x");

            using var app = Start(sandbox);
            try
            {
                Click(Retry(() => FindWindow(app.Id, "Welcome to Pickets"), timeoutMs: 30000), "Organize my desktop for me");
                // "Auto-import complete" (a message box, also called Pickets).
                var done = Retry(() => FindWindow(app.Id, "Pickets", w => w.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "OK"))) != null));
                Click(done, "OK");

                Assert.Equal(new[] { "Setup.exe" }, TileNames(WaitForWindow(app.Id, "Apps fence"), "Apps"));
                Assert.Equal(new[] { "Notes.txt" }, TileNames(WaitForWindow(app.Id, "Documents fence"), "Documents"));
                Assert.Null(FindWindow(app.Id, "Desktop fence"));
            }
            finally
            {
                if (!app.HasExited) app.Kill();
                Ui.DeleteTempFolders();
            }
        }

        private static (string Sandbox, string Desktop, string Data) NewSandbox()
        {
            var sandbox = Ui.TempFolder();
            var desktop = Directory.CreateDirectory(Path.Combine(sandbox, "Desktop")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(sandbox, "AppData")).FullName;
            return (sandbox, desktop, data);
        }

        private static Process Start(string sandbox)
        {
            var start = new ProcessStartInfo(PicketsExe()) { UseShellExecute = false };
            start.Environment["PICKETS_SANDBOX"] = sandbox;
            return Process.Start(start)!;
        }

        // A window of the app by name, top-level or owned by another of its windows (dialogs
        // such as the welcome show up inside the window that owns them).
        private static AutomationElement? FindWindow(int processId, string name, Func<AutomationElement, bool>? where = null)
        {
            var named = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, name));
            var tops = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId)).Cast<AutomationElement>().ToList();
            return tops.Where(t => t.Current.Name == name)
                       .Concat(tops.SelectMany(t => t.FindAll(TreeScope.Descendants, named).Cast<AutomationElement>()))
                       .FirstOrDefault(w => where == null || where(w));
        }

        private static void Click(AutomationElement window, string button)
        {
            var found = Retry(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, button))));
            ((InvokePattern)found.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }

        private static string[] TileNames(AutomationElement fenceWindow, string listName)
        {
            var list = fenceWindow.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                new PropertyCondition(AutomationElement.NameProperty, listName)));
            Assert.NotNull(list);
            return Retry(() =>
            {
                var names = list.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                    .Cast<AutomationElement>().Select(e => e.Current.Name).ToArray();
                return names.Length > 0 ? names : null;
            });
        }

        private static AutomationElement WaitForWindow(int processId, string name) =>
            Retry(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.NameProperty, name))), timeoutMs: 30000)
            ?? throw new Exception($"No window called “{name}” appeared.");

        private static T Retry<T>(Func<T?> attempt, int timeoutMs = 10000) where T : class
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                try { if (attempt() is T found) return found; }
                catch (ElementNotAvailableException) { /* the UI changed under us; look again */ }
                if (DateTime.UtcNow > until) throw new TimeoutException("Gave up waiting for the UI.");
                Thread.Sleep(200);
            }
        }

        // The app as built next to these tests (same configuration).
        private static string PicketsExe()
        {
            var here = AppContext.BaseDirectory; // tests\Pickets.UiTests\bin\<Configuration>\net8.0-windows\
            var dir = new DirectoryInfo(here);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Pickets.sln"))) dir = dir.Parent;
            if (dir == null) throw new FileNotFoundException("Couldn't find the repository root from " + here);
            var config = new DirectoryInfo(here.TrimEnd('\\')).Parent!.Name;
            var exe = Path.Combine(dir.FullName, "Pickets", "bin", config, "net8.0-windows", "Pickets.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Build Pickets first.", exe);
            return exe;
        }
    }
}
