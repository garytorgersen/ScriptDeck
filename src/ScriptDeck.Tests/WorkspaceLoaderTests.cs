using System;
using System.Collections.Generic;
using System.IO;
using ScriptDeck.Workspace;
using Xunit;

namespace ScriptDeck.Tests
{
    public class WorkspaceLoaderTests : IDisposable
    {
        private readonly string _tmpDir;

        public WorkspaceLoaderTests()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "ScriptDeckTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
            catch { }
        }

        private string TmpFile(string name) => Path.Combine(_tmpDir, name);

        [Fact]
        public void Save_Then_Load_Roundtrips_All_Fields()
        {
            var path = TmpFile("ws.json");
            var ws = new Workspace.Workspace
            {
                Name = "Test",
                SharedInputs = new List<SharedInput>
                {
                    new SharedInput { Id = "x", Label = "X", Type = "text", Default = "v", Normalize = "computerName" }
                },
                Tabs = new List<Tab>
                {
                    new Tab { Id = "t1", Title = "Tab 1",
                        Buttons = new List<Workspace.Button>
                        {
                            new Workspace.Button { Id = "b1", Label = "Run", Executor = "powershell",
                                ScriptPath = "scripts\\foo.ps1", Args = new List<string> { "-X", "1" },
                                Outputs = new List<string> { "rtb", "grid" }, Confirm = true,
                                RtbFormat = "table", RunInBackground = true,
                                X = 10, Y = 20, Width = 150, Height = 36 }
                        },
                        Groups = new List<ButtonGroup>
                        {
                            new ButtonGroup { Id = "g1", Title = "Group", X = 5, Y = 5, Width = 200, Height = 100 }
                        }
                    }
                },
                Menus = new List<MenuDefinition>
                {
                    new MenuDefinition { Title = "&Quick",
                        Items = new List<Workspace.Button>
                        {
                            new Workspace.Button { Id = "q1", Label = "First", Executor = "process", ScriptPath = "C:\\foo.exe" }
                        }
                    }
                }
            };

            WorkspaceLoader.Save(ws, path);
            Assert.True(File.Exists(path));

            var loaded = WorkspaceLoader.Load(path);
            Assert.Equal("Test", loaded.Name);
            Assert.Single(loaded.SharedInputs);
            Assert.Equal("computerName", loaded.SharedInputs[0].Normalize);
            Assert.Single(loaded.Tabs);
            Assert.Single(loaded.Tabs[0].Buttons);
            Assert.Single(loaded.Tabs[0].Groups);
            Assert.Equal("table", loaded.Tabs[0].Buttons[0].RtbFormat);
            Assert.True(loaded.Tabs[0].Buttons[0].RunInBackground);
            Assert.Single(loaded.Menus);
            Assert.Equal("&Quick", loaded.Menus[0].Title);
        }

        [Fact]
        public void Load_Refuses_Newer_Schema_Version()
        {
            var path = TmpFile("future.json");
            var futureSchema = "{ \"version\": 999, \"name\": \"future\" }";
            File.WriteAllText(path, futureSchema);
            Assert.Throws<InvalidDataException>(() => WorkspaceLoader.Load(path));
        }

        [Fact]
        public void Load_Throws_On_Missing_File()
        {
            Assert.Throws<FileNotFoundException>(() => WorkspaceLoader.Load(TmpFile("nope.json")));
        }

        [Fact]
        public void Load_Defaults_ScriptsRoot_To_File_Directory()
        {
            var path = TmpFile("ws.json");
            File.WriteAllText(path, "{ \"name\": \"X\" }");
            var loaded = WorkspaceLoader.Load(path);
            Assert.Equal(_tmpDir, loaded.ScriptsRoot, ignoreCase: true);
        }

        [Fact]
        public void Load_Honors_Explicit_ScriptsRoot()
        {
            var path = TmpFile("ws.json");
            File.WriteAllText(path, "{ \"name\": \"X\", \"scriptsRoot\": \"C:/explicit\" }");
            var loaded = WorkspaceLoader.Load(path);
            Assert.Equal("C:/explicit", loaded.ScriptsRoot);
        }

        [Fact]
        public void Load_Null_Fills_Collections()
        {
            // A hand-edited JSON with `"buttons": null` would otherwise
            // NRE the renderer. Loader is supposed to backfill empty lists.
            var path = TmpFile("nulls.json");
            File.WriteAllText(path,
                "{ \"name\": \"X\", \"sharedInputs\": null, \"tabs\": null, \"menus\": null }");
            var loaded = WorkspaceLoader.Load(path);
            Assert.NotNull(loaded.SharedInputs);
            Assert.NotNull(loaded.Tabs);
            Assert.NotNull(loaded.Menus);
            Assert.Empty(loaded.SharedInputs);
        }

        [Fact]
        public void Load_Null_Fills_Tab_Groups_And_Buttons()
        {
            // H10 fix: Tabs[i].Groups was previously null after load.
            var path = TmpFile("tabnulls.json");
            File.WriteAllText(path,
                "{ \"name\": \"X\", \"tabs\": [{ \"id\": \"t\", \"buttons\": null, \"groups\": null }] }");
            var loaded = WorkspaceLoader.Load(path);
            Assert.NotNull(loaded.Tabs[0].Buttons);
            Assert.NotNull(loaded.Tabs[0].Groups);
        }

        [Fact]
        public void Save_Replaces_Existing_File_Atomically()
        {
            // C3 fix: Save should NEVER leave the user without a workspace
            // file, even if the second save partially fails (we can't
            // simulate the crash here, but we can verify the happy path
            // and that .bak doesn't accumulate).
            var path = TmpFile("ws.json");
            var ws1 = new Workspace.Workspace { Name = "first" };
            WorkspaceLoader.Save(ws1, path);
            var ws2 = new Workspace.Workspace { Name = "second" };
            WorkspaceLoader.Save(ws2, path);
            Assert.True(File.Exists(path));
            // .bak should be cleaned up after a successful Replace.
            Assert.False(File.Exists(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
            var loaded = WorkspaceLoader.Load(path);
            Assert.Equal("second", loaded.Name);
        }

        [Fact]
        public void Save_Creates_Parent_Directory_If_Missing()
        {
            var path = Path.Combine(_tmpDir, "subdir", "ws.json");
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
            WorkspaceLoader.Save(new Workspace.Workspace { Name = "x" }, path);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void Load_Throws_On_Malformed_Json()
        {
            var path = TmpFile("bad.json");
            File.WriteAllText(path, "{ this is not valid }");
            Assert.ThrowsAny<Exception>(() => WorkspaceLoader.Load(path));
        }

        [Fact]
        public void Save_Stamps_Current_Schema_Version()
        {
            var path = TmpFile("ws.json");
            var ws = new Workspace.Workspace { Name = "x", Version = 0 };
            WorkspaceLoader.Save(ws, path);
            Assert.Equal(WorkspaceLoader.CurrentSchemaVersion, ws.Version);
            var loaded = WorkspaceLoader.Load(path);
            Assert.Equal(WorkspaceLoader.CurrentSchemaVersion, loaded.Version);
        }
    }
}
