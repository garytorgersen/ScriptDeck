using System;
using System.Linq;
using System.Windows.Forms;
using ScriptDeck.Workspace;

namespace ScriptDeck.Forms
{
    /// <summary>
    /// Simple modal for renaming a tab or creating a new one. Mutates a
    /// caller-supplied <see cref="Tab"/> on OK; leaves it untouched on
    /// Cancel — same copy-on-OK pattern as <see cref="EditButtonDialog"/>.
    /// </summary>
    public partial class EditTabDialog : Form
    {
        public EditTabDialog(Tab source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            InitializeComponent();
            textBox_Id.Text    = source.Id ?? string.Empty;
            textBox_Title.Text = source.Title ?? string.Empty;
        }

        public void ApplyTo(Tab target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Id    = textBox_Id.Text.Trim();
            target.Title = textBox_Title.Text;
        }

        private void Button_Ok_Click(object sender, EventArgs e)
        {
            // Title is what the user sees on the tab strip — empty title
            // would render an unclickable sliver. Reject up-front.
            if (string.IsNullOrWhiteSpace(textBox_Title.Text))
            {
                MessageBox.Show(this, "Title is required.", "ScriptDeck",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                textBox_Title.Focus();
                this.DialogResult = DialogResult.None;
                return;
            }
            // Auto-id from title — saves a step. Lowercased and slugified
            // so it's a sane JSON-key-shaped string.
            if (string.IsNullOrWhiteSpace(textBox_Id.Text))
                textBox_Id.Text = SlugifyId(textBox_Title.Text);
        }

        private static string SlugifyId(string title)
        {
            if (string.IsNullOrEmpty(title)) return "tab";
            var chars = title.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-')
                .ToArray();
            var slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return string.IsNullOrEmpty(slug) ? "tab" : slug;
        }
    }
}
