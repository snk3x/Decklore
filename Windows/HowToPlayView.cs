namespace TcgScanner;

/// <summary>Shows a how-to-play guide in the Windows app.</summary>
public static class HowToPlayView
{
    /// <summary>Writes a guide into a RichTextBox with headings, bullets and bold terms.</summary>
    public static void Render(RichTextBox box, RulesGuide guide, Font baseFont)
    {
        using var titleFont = new Font(baseFont.FontFamily, baseFont.Size + 7, FontStyle.Bold);
        using var headFont = new Font(baseFont.FontFamily, baseFont.Size + 2.5f, FontStyle.Bold);
        using var boldFont = new Font(baseFont, FontStyle.Bold);
        var accent = Color.FromArgb(40, 60, 140);

        box.SuspendLayout();
        box.Clear();

        void Write(string text, Font font, Color color, int indent = 0, int hanging = 0, bool bullet = false)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionIndent = indent;
            box.SelectionHangingIndent = hanging;
            box.SelectionBullet = bullet;
            box.SelectionFont = font;
            box.SelectionColor = color;
            box.AppendText(text);
        }

        void WriteRich(string line, int indent, bool bullet)
        {
            var parts = line.Split("**");
            for (int i = 0; i < parts.Length; i++)
                Write(parts[i], i % 2 == 1 ? boldFont : baseFont, SystemColors.WindowText, indent, 0, bullet);
            Write("\n", baseFont, SystemColors.WindowText, indent, 0, bullet);
        }

        Write(guide.Title + "\n", titleFont, accent);
        Write(guide.Intro + "\n", baseFont, SystemColors.WindowText);
        foreach (var (heading, lines) in guide.Sections)
        {
            Write("\n" + heading + "\n", headFont, accent);
            foreach (var line in lines)
            {
                bool bullet = line.StartsWith("• ");
                WriteRich(bullet ? line[2..] : line, bullet ? 12 : 0, bullet);
            }
        }
        Write("\nThis guide is a summary for learning. The official rules have the full details and the final word.\n",
              new Font(baseFont, FontStyle.Italic), Color.DimGray);
        box.SelectionStart = 0;
        box.ScrollToCaret();
        box.ResumeLayout();
    }
}
