using System.Windows.Forms;

namespace ScriptDeck.Forms
{
    partial class EditMenusDialog
    {
        private System.ComponentModel.IContainer components = null;

        private SplitContainer splitContainer_Main;
        private TreeView treeView_Menus;
        private FlowLayoutPanel panel_Actions;
        private System.Windows.Forms.Button button_AddMenu;
        private System.Windows.Forms.Button button_RenameMenu;
        private System.Windows.Forms.Button button_DeleteMenu;
        private Label label_ActionsSep1;
        private System.Windows.Forms.Button button_AddItem;
        private System.Windows.Forms.Button button_AddSeparator;
        private System.Windows.Forms.Button button_EditItem;
        private System.Windows.Forms.Button button_DeleteItem;
        private Label label_ActionsSep2;
        private System.Windows.Forms.Button button_MoveUp;
        private System.Windows.Forms.Button button_MoveDown;
        private Label label_Hint;

        private FlowLayoutPanel panel_OkCancel;
        private System.Windows.Forms.Button button_Ok;
        private System.Windows.Forms.Button button_Cancel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            this.splitContainer_Main = new SplitContainer();
            this.treeView_Menus = new TreeView();
            this.panel_Actions = new FlowLayoutPanel();
            this.button_AddMenu = new System.Windows.Forms.Button();
            this.button_RenameMenu = new System.Windows.Forms.Button();
            this.button_DeleteMenu = new System.Windows.Forms.Button();
            this.label_ActionsSep1 = new Label();
            this.button_AddItem = new System.Windows.Forms.Button();
            this.button_AddSeparator = new System.Windows.Forms.Button();
            this.button_EditItem = new System.Windows.Forms.Button();
            this.button_DeleteItem = new System.Windows.Forms.Button();
            this.label_ActionsSep2 = new Label();
            this.button_MoveUp = new System.Windows.Forms.Button();
            this.button_MoveDown = new System.Windows.Forms.Button();
            this.label_Hint = new Label();

            this.panel_OkCancel = new FlowLayoutPanel();
            this.button_Ok = new System.Windows.Forms.Button();
            this.button_Cancel = new System.Windows.Forms.Button();

            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Main)).BeginInit();
            this.splitContainer_Main.Panel1.SuspendLayout();
            this.splitContainer_Main.Panel2.SuspendLayout();
            this.splitContainer_Main.SuspendLayout();
            this.panel_Actions.SuspendLayout();
            this.panel_OkCancel.SuspendLayout();
            this.SuspendLayout();

            //
            // splitContainer_Main
            //
            // Vertical split: tree on the left, action buttons on the right.
            // FixedPanel.Panel2 keeps the action column from changing width
            // when the dialog resizes — buttons feel anchored.
            this.splitContainer_Main.Dock = DockStyle.Fill;
            this.splitContainer_Main.Orientation = Orientation.Vertical;
            this.splitContainer_Main.FixedPanel = FixedPanel.Panel2;
            // Explicit Size BEFORE SplitterDistance + Panel2MinSize. The
            // SplitContainer's design-time default (150x100) is narrower
            // than SplitterDistance(380) + Panel2MinSize(170) + splitter,
            // so the validator throws InvalidOperationException at
            // EndInit. Dock=Fill overrides this on first layout — the
            // value here just has to satisfy the validator.
            this.splitContainer_Main.Size = new System.Drawing.Size(700, 460);
            this.splitContainer_Main.SplitterDistance = 380;
            this.splitContainer_Main.Panel2MinSize = 170;
            this.splitContainer_Main.Panel1.Controls.Add(this.treeView_Menus);
            this.splitContainer_Main.Panel2.Controls.Add(this.panel_Actions);

            //
            // treeView_Menus
            //
            // Two-level tree: root nodes are menus, children are items.
            // HideSelection=false so the highlight stays visible while
            // the action buttons are clicked.
            this.treeView_Menus.Dock = DockStyle.Fill;
            this.treeView_Menus.HideSelection = false;
            this.treeView_Menus.FullRowSelect = true;
            this.treeView_Menus.ShowLines = true;
            this.treeView_Menus.Indent = 22;
            this.treeView_Menus.Name = "treeView_Menus";
            this.treeView_Menus.AfterSelect += new TreeViewEventHandler(this.TreeView_Menus_AfterSelect);
            this.treeView_Menus.NodeMouseDoubleClick += new TreeNodeMouseClickEventHandler(this.TreeView_Menus_NodeMouseDoubleClick);

            //
            // panel_Actions
            //
            // Top-down stack of buttons grouped: menu actions, item actions,
            // ordering, then a hint label. FlowLayout with TopDown direction
            // keeps the column tidy without wrestling with anchors.
            this.panel_Actions.Dock = DockStyle.Fill;
            this.panel_Actions.FlowDirection = FlowDirection.TopDown;
            this.panel_Actions.Padding = new Padding(8, 8, 8, 8);
            this.panel_Actions.WrapContents = false;
            this.panel_Actions.AutoScroll = true;

            ConfigureActionButton(this.button_AddMenu,      "Add Menu",      Button_AddMenu_Click);
            ConfigureActionButton(this.button_RenameMenu,   "Rename Menu",   Button_RenameMenu_Click);
            ConfigureActionButton(this.button_DeleteMenu,   "Delete Menu",   Button_DeleteMenu_Click);

            this.label_ActionsSep1.AutoSize = true;
            this.label_ActionsSep1.Text = "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500";
            this.label_ActionsSep1.ForeColor = System.Drawing.SystemColors.ControlDark;
            this.label_ActionsSep1.Margin = new Padding(0, 6, 0, 6);

            ConfigureActionButton(this.button_AddItem,      "Add Item...",   Button_AddItem_Click);
            ConfigureActionButton(this.button_AddSeparator, "Add Separator", Button_AddSeparator_Click);
            ConfigureActionButton(this.button_EditItem,     "Edit Item...",  Button_EditItem_Click);
            ConfigureActionButton(this.button_DeleteItem,   "Delete Item",   Button_DeleteItem_Click);

            this.label_ActionsSep2.AutoSize = true;
            this.label_ActionsSep2.Text = "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500";
            this.label_ActionsSep2.ForeColor = System.Drawing.SystemColors.ControlDark;
            this.label_ActionsSep2.Margin = new Padding(0, 6, 0, 6);

            ConfigureActionButton(this.button_MoveUp,   "Move Up",   Button_MoveUp_Click);
            ConfigureActionButton(this.button_MoveDown, "Move Down", Button_MoveDown_Click);

            this.label_Hint.AutoSize = false;
            this.label_Hint.Width = 150;
            this.label_Hint.Height = 84;
            this.label_Hint.Margin = new Padding(0, 12, 0, 0);
            this.label_Hint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.label_Hint.Text =
                "Tip: items run through the same dispatcher as tab buttons. " +
                "Double-click an item to edit. Separators show as a divider line in the menu.";

            this.panel_Actions.Controls.Add(this.button_AddMenu);
            this.panel_Actions.Controls.Add(this.button_RenameMenu);
            this.panel_Actions.Controls.Add(this.button_DeleteMenu);
            this.panel_Actions.Controls.Add(this.label_ActionsSep1);
            this.panel_Actions.Controls.Add(this.button_AddItem);
            this.panel_Actions.Controls.Add(this.button_AddSeparator);
            this.panel_Actions.Controls.Add(this.button_EditItem);
            this.panel_Actions.Controls.Add(this.button_DeleteItem);
            this.panel_Actions.Controls.Add(this.label_ActionsSep2);
            this.panel_Actions.Controls.Add(this.button_MoveUp);
            this.panel_Actions.Controls.Add(this.button_MoveDown);
            this.panel_Actions.Controls.Add(this.label_Hint);

            //
            // panel_OkCancel
            //
            this.panel_OkCancel.Dock = DockStyle.Bottom;
            this.panel_OkCancel.FlowDirection = FlowDirection.RightToLeft;
            this.panel_OkCancel.Height = 42;
            this.panel_OkCancel.Padding = new Padding(8, 8, 8, 8);
            this.panel_OkCancel.Controls.Add(this.button_Cancel);
            this.panel_OkCancel.Controls.Add(this.button_Ok);

            this.button_Ok.Text = "OK";
            this.button_Ok.Width = 90;
            this.button_Ok.DialogResult = DialogResult.OK;

            this.button_Cancel.Text = "Cancel";
            this.button_Cancel.Width = 90;
            this.button_Cancel.DialogResult = DialogResult.Cancel;

            //
            // EditMenusDialog
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(620, 460);
            this.Controls.Add(this.splitContainer_Main);
            this.Controls.Add(this.panel_OkCancel);
            this.MinimumSize = new System.Drawing.Size(560, 380);
            this.Name = "EditMenusDialog";
            this.Text = "Edit Workspace Menus";
            this.StartPosition = FormStartPosition.CenterParent;
            this.ShowInTaskbar = false;
            this.AcceptButton = this.button_Ok;
            this.CancelButton = this.button_Cancel;

            this.splitContainer_Main.Panel1.ResumeLayout(false);
            this.splitContainer_Main.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitContainer_Main)).EndInit();
            this.splitContainer_Main.ResumeLayout(false);
            this.panel_Actions.ResumeLayout(false);
            this.panel_Actions.PerformLayout();
            this.panel_OkCancel.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        // Helper: every action button is the same width / margin, so this
        // collapses the boilerplate. Wired here in Designer code rather
        // than in code-behind because the click handlers ARE the entire
        // public surface of these buttons.
        private void ConfigureActionButton(
            System.Windows.Forms.Button btn, string text, System.EventHandler onClick)
        {
            btn.Text = text;
            btn.Width = 150;
            btn.Height = 28;
            btn.Margin = new Padding(0, 0, 0, 4);
            btn.UseVisualStyleBackColor = true;
            btn.Click += onClick;
        }
    }
}
