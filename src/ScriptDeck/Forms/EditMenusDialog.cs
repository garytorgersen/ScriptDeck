using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using ScriptDeck.Workspace;

using WsButton = ScriptDeck.Workspace.Button;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Modal editor for the workspace's top-level menu strip. Mirrors the
    /// pattern used by <see cref="EditButtonDialog"/>: we mutate a deep
    /// clone of the caller's list, so a Cancel really discards every edit
    /// even after extensive tree manipulation. On OK, the caller pulls the
    /// working list via <see cref="GetEditedList"/>.
    ///
    /// Tree shape:
    ///   root nodes      = MenuDefinition  (Tag = MenuDefinition)
    ///   child nodes     = WsButton        (Tag = WsButton; Label "-" → separator)
    /// </summary>
    public partial class EditMenusDialog : Form
    {
        private readonly string _scriptsRoot;
        private readonly List<MenuDefinition> _working;

        public EditMenusDialog(IList<MenuDefinition> source, string scriptsRoot)
        {
            InitializeComponent();
            _scriptsRoot = scriptsRoot;
            _working = CloneList(source);
            RebuildTree(null);
            UpdateButtonStates();
        }

        /// <summary>The edited list. Caller assigns this onto the workspace on OK.</summary>
        public IList<MenuDefinition> GetEditedList() => _working;

        // ---- cloning ----

        // Deep clone so Cancel discards everything. We never share button
        // instances with the workspace until OK — see class-level comment.
        private static List<MenuDefinition> CloneList(IList<MenuDefinition> source)
        {
            var copy = new List<MenuDefinition>();
            if (source == null) return copy;
            foreach (var m in source)
            {
                if (m == null) continue;
                var nm = new MenuDefinition
                {
                    Title = m.Title,
                    Items = new List<WsButton>(),
                };
                if (m.Items != null)
                {
                    foreach (var b in m.Items)
                    {
                        if (b == null) continue;
                        nm.Items.Add(CloneButton(b));
                    }
                }
                copy.Add(nm);
            }
            return copy;
        }

        private static WsButton CloneButton(WsButton b)
        {
            return new WsButton
            {
                Id               = b.Id,
                Label            = b.Label,
                Executor         = b.Executor,
                ScriptPath       = b.ScriptPath,
                Args             = b.Args != null ? new List<string>(b.Args) : new List<string>(),
                WorkingDirectory = b.WorkingDirectory,
                Outputs          = b.Outputs != null ? new List<string>(b.Outputs) : new List<string> { "rtb" },
                Confirm          = b.Confirm,
                Log              = b.Log,
                ExtendedGridData = b.ExtendedGridData,
            };
        }

        // ---- tree population ----

        // Repopulate from _working. preserveSelection (by reference) lets
        // us keep the user's place across structural mutations — picking
        // by index would jump around as items move, get added, or get
        // removed.
        private void RebuildTree(object preserveSelection)
        {
            treeView_Menus.BeginUpdate();
            try
            {
                treeView_Menus.Nodes.Clear();
                TreeNode toSelect = null;

                foreach (var menu in _working)
                {
                    var menuNode = new TreeNode(string.IsNullOrEmpty(menu.Title) ? "(untitled menu)" : menu.Title)
                    {
                        Tag = menu,
                    };
                    if (ReferenceEquals(menu, preserveSelection)) toSelect = menuNode;

                    foreach (var item in menu.Items)
                    {
                        var itemNode = new TreeNode(DescribeItem(item)) { Tag = item };
                        if (ReferenceEquals(item, preserveSelection)) toSelect = itemNode;
                        menuNode.Nodes.Add(itemNode);
                    }
                    menuNode.Expand();
                    treeView_Menus.Nodes.Add(menuNode);
                }

                if (toSelect != null) treeView_Menus.SelectedNode = toSelect;
            }
            finally
            {
                treeView_Menus.EndUpdate();
            }
            UpdateButtonStates();
        }

        // Mirrors the dispatcher convention: a button whose Label is "-"
        // renders as a separator in the live menu, so we visualize it the
        // same way in the editor.
        private static string DescribeItem(WsButton item)
        {
            if (item == null) return "(null)";
            if (string.Equals(item.Label, "-", StringComparison.Ordinal)) return "\u2500\u2500\u2500 (separator)";
            var label = string.IsNullOrEmpty(item.Label) ? "(unnamed)" : item.Label;
            return string.IsNullOrEmpty(item.Executor) ? label : $"{label}  [{item.Executor}]";
        }

        // ---- selection state ----

        // Maintains the rule "you can't Edit/Delete an item unless an item
        // is selected, and you can't Rename/Delete a menu unless a menu
        // is selected". Move buttons depend on having siblings in the
        // appropriate direction.
        private void UpdateButtonStates()
        {
            var node = treeView_Menus.SelectedNode;
            bool isMenu = node != null && node.Tag is MenuDefinition;
            bool isItem = node != null && node.Tag is WsButton;

            button_RenameMenu.Enabled = isMenu;
            button_DeleteMenu.Enabled = isMenu;

            // Adding items requires a containing menu. We allow it when an
            // item is selected too (treating its parent as the target),
            // since users naturally click an item and then "Add Item".
            button_AddItem.Enabled      = isMenu || isItem;
            button_AddSeparator.Enabled = isMenu || isItem;
            button_EditItem.Enabled     = isItem && !IsSeparator(node);
            button_DeleteItem.Enabled   = isItem;

            // Move within the same parent. Different rules for menus vs items.
            if (isMenu)
            {
                int idx = treeView_Menus.Nodes.IndexOf(node);
                button_MoveUp.Enabled   = idx > 0;
                button_MoveDown.Enabled = idx >= 0 && idx < treeView_Menus.Nodes.Count - 1;
            }
            else if (isItem)
            {
                var parent = node.Parent;
                int idx = parent != null ? parent.Nodes.IndexOf(node) : -1;
                button_MoveUp.Enabled   = idx > 0;
                button_MoveDown.Enabled = parent != null && idx >= 0 && idx < parent.Nodes.Count - 1;
            }
            else
            {
                button_MoveUp.Enabled = false;
                button_MoveDown.Enabled = false;
            }
        }

        private static bool IsSeparator(TreeNode n)
        {
            var b = n?.Tag as WsButton;
            return b != null && string.Equals(b.Label, "-", StringComparison.Ordinal);
        }

        // Resolve "the menu I should add into" — the selected menu, or
        // the parent menu of the selected item. Returns null if neither.
        private MenuDefinition GetTargetMenu(out int insertAfterIndex)
        {
            insertAfterIndex = -1;
            var node = treeView_Menus.SelectedNode;
            if (node == null) return null;
            if (node.Tag is MenuDefinition m)
            {
                insertAfterIndex = m.Items.Count - 1; // append
                return m;
            }
            if (node.Tag is WsButton b && node.Parent?.Tag is MenuDefinition pm)
            {
                insertAfterIndex = pm.Items.IndexOf(b); // insert just after selected
                return pm;
            }
            return null;
        }

        // ---- event handlers ----

        private void TreeView_Menus_AfterSelect(object sender, TreeViewEventArgs e)
        {
            UpdateButtonStates();
        }

        private void TreeView_Menus_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node?.Tag is WsButton btn && !string.Equals(btn.Label, "-", StringComparison.Ordinal))
            {
                EditItem(btn);
            }
            else if (e.Node?.Tag is MenuDefinition menu)
            {
                RenameMenu(menu);
            }
        }

        private void Button_AddMenu_Click(object sender, EventArgs e)
        {
            string title = PromptForString("New menu title:", "Add Menu", "New Menu");
            if (title == null) return;
            var menu = new MenuDefinition { Title = title, Items = new List<WsButton>() };
            _working.Add(menu);
            RebuildTree(menu);
        }

        private void Button_RenameMenu_Click(object sender, EventArgs e)
        {
            if (treeView_Menus.SelectedNode?.Tag is MenuDefinition m) RenameMenu(m);
        }

        private void RenameMenu(MenuDefinition m)
        {
            string title = PromptForString("Menu title:", "Rename Menu", m.Title ?? string.Empty);
            if (title == null) return;
            m.Title = title;
            RebuildTree(m);
        }

        private void Button_DeleteMenu_Click(object sender, EventArgs e)
        {
            if (!(treeView_Menus.SelectedNode?.Tag is MenuDefinition m)) return;
            int itemCount = m.Items?.Count ?? 0;
            // Confirm because deleting a populated menu drops every item
            // beneath it — same destructive-action policy as everywhere
            // else in ScriptDeck.
            string msg = itemCount > 0
                ? $"Delete menu '{m.Title}' and its {itemCount} item(s)?"
                : $"Delete menu '{m.Title}'?";
            if (MessageBox.Show(this, msg, "Delete Menu",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            _working.Remove(m);
            RebuildTree(null);
        }

        private void Button_AddItem_Click(object sender, EventArgs e)
        {
            var menu = GetTargetMenu(out int afterIdx);
            if (menu == null) return;
            var fresh = new WsButton
            {
                Label = "New Item",
                Executor = "powershell",
                Outputs = new List<string> { "rtb" },
                Log = true,
            };
            using (var dlg = new EditButtonDialog(fresh, _scriptsRoot))
            {
                dlg.Text = "Add Menu Item";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(fresh);
            }
            int insertAt = Math.Min(menu.Items.Count, afterIdx + 1);
            menu.Items.Insert(insertAt, fresh);
            RebuildTree(fresh);
        }

        private void Button_AddSeparator_Click(object sender, EventArgs e)
        {
            var menu = GetTargetMenu(out int afterIdx);
            if (menu == null) return;
            // Convention: Label "-" is the on-disk marker for a separator.
            // Other fields are unused at render time but we set Executor
            // anyway so a hand-edited file with a stray separator still
            // round-trips through validation.
            var sep = new WsButton { Label = "-", Executor = "powershell" };
            int insertAt = Math.Min(menu.Items.Count, afterIdx + 1);
            menu.Items.Insert(insertAt, sep);
            RebuildTree(sep);
        }

        private void Button_EditItem_Click(object sender, EventArgs e)
        {
            if (treeView_Menus.SelectedNode?.Tag is WsButton b && !string.Equals(b.Label, "-", StringComparison.Ordinal))
                EditItem(b);
        }

        private void EditItem(WsButton b)
        {
            using (var dlg = new EditButtonDialog(b, _scriptsRoot))
            {
                dlg.Text = "Edit Menu Item";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                dlg.ApplyTo(b);
            }
            RebuildTree(b);
        }

        private void Button_DeleteItem_Click(object sender, EventArgs e)
        {
            if (!(treeView_Menus.SelectedNode?.Tag is WsButton b)) return;
            var parent = treeView_Menus.SelectedNode.Parent?.Tag as MenuDefinition;
            if (parent == null) return;

            string label = string.Equals(b.Label, "-", StringComparison.Ordinal) ? "this separator" : $"'{b.Label}'";
            if (MessageBox.Show(this, $"Delete {label}?", "Delete Item",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            // Try to keep the user near where they were: select the next
            // sibling, or fall back to the parent menu.
            int idx = parent.Items.IndexOf(b);
            parent.Items.Remove(b);
            object next = (idx >= 0 && idx < parent.Items.Count) ? (object)parent.Items[idx]
                        : (idx > 0 ? (object)parent.Items[idx - 1] : (object)parent);
            RebuildTree(next);
        }

        private void Button_MoveUp_Click(object sender, EventArgs e) => MoveSelection(-1);
        private void Button_MoveDown_Click(object sender, EventArgs e) => MoveSelection(+1);

        // Named MoveSelection rather than Move to avoid shadowing the
        // inherited Control.Move event — the compiler warns on the shadow,
        // and even renamed it's clearer what's moving.
        private void MoveSelection(int delta)
        {
            var node = treeView_Menus.SelectedNode;
            if (node == null) return;

            if (node.Tag is MenuDefinition menu)
            {
                int i = _working.IndexOf(menu);
                int j = i + delta;
                if (i < 0 || j < 0 || j >= _working.Count) return;
                _working.RemoveAt(i);
                _working.Insert(j, menu);
                RebuildTree(menu);
            }
            else if (node.Tag is WsButton item && node.Parent?.Tag is MenuDefinition parent)
            {
                int i = parent.Items.IndexOf(item);
                int j = i + delta;
                if (i < 0 || j < 0 || j >= parent.Items.Count) return;
                parent.Items.RemoveAt(i);
                parent.Items.Insert(j, item);
                RebuildTree(item);
            }
        }

        // ---- inline prompt ----

        // Tiny single-field prompt. Avoids pulling in Microsoft.VisualBasic
        // for InputBox (we don't reference that assembly anywhere else and
        // dragging it in just for a 2-line dialog isn't worth it). Returns
        // null on Cancel; empty/whitespace input is rejected so we don't
        // create blank-titled menus that can't be navigated to.
        private string PromptForString(string prompt, string title, string initial)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.ClientSize = new System.Drawing.Size(360, 120);

                var lbl = new Label { Text = prompt, Left = 12, Top = 12, AutoSize = true };
                var tb  = new TextBox { Left = 12, Top = 36, Width = 336, Text = initial ?? string.Empty };
                var ok  = new System.Windows.Forms.Button { Text = "OK", DialogResult = DialogResult.OK,
                              Left = 192, Top = 76, Width = 75 };
                var cn  = new System.Windows.Forms.Button { Text = "Cancel", DialogResult = DialogResult.Cancel,
                              Left = 273, Top = 76, Width = 75 };

                f.Controls.AddRange(new Control[] { lbl, tb, ok, cn });
                f.AcceptButton = ok;
                f.CancelButton = cn;

                tb.SelectAll();
                if (f.ShowDialog(this) != DialogResult.OK) return null;
                var s = tb.Text?.Trim();
                if (string.IsNullOrEmpty(s)) return null;
                return s;
            }
        }
    }
}
