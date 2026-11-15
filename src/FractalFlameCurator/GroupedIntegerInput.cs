using System.Globalization;
using System.Text;
using System.Windows.Input;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace FractalFlameCurator;

public static class GroupedIntegerInput
{
    public static void Attach(WpfTextBox box)
    {
        box.TextChanged += (_, _) => FormatBox(box);
        box.PreviewKeyDown += (_, e) =>
        {
            if (box.SelectionLength != 0) return;
            var position = box.CaretIndex;
            if (e.Key == Key.Back && position >= 2 && box.Text[position - 1] == ' ' && char.IsAsciiDigit(box.Text[position - 2]))
            {
                box.Select(position - 2, 1);
                box.SelectedText = string.Empty;
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && position + 1 < box.Text.Length && box.Text[position] == ' ' && char.IsAsciiDigit(box.Text[position + 1]))
            {
                box.Select(position + 1, 1);
                box.SelectedText = string.Empty;
                e.Handled = true;
            }
        };
        FormatBox(box);
    }

    public static bool TryParse(string? value, out long result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !char.IsAsciiDigit(c) && !char.IsWhiteSpace(c))) return false;
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    public static string Format(long value) => FormatDigits(value.ToString(CultureInfo.InvariantCulture));

    public static long Parse(string? text, string name, long minimum, long maximum) =>
        TryParse(text, out var value) && value >= minimum && value <= maximum
            ? value
            : throw new InvalidOperationException($"{name} must be a whole number from {Format(minimum)} to {Format(maximum)}.");

    private static string FormatDigits(string digits)
    {
        var formatted = new StringBuilder(digits.Length + digits.Length / 3);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) formatted.Append(' ');
            formatted.Append(digits[i]);
        }
        return formatted.ToString();
    }

    private static void FormatBox(WpfTextBox box)
    {
        var text = box.Text;
        if (text.Length == 0 || text.Any(c => !char.IsAsciiDigit(c) && !char.IsWhiteSpace(c))) return;
        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return;
        var formatted = FormatDigits(digits);
        if (formatted == text) return;
        var beforeCaret = text.Take(box.CaretIndex).Count(char.IsAsciiDigit);
        box.Text = formatted;
        var position = 0;
        while (position < formatted.Length && beforeCaret > 0)
        {
            if (char.IsAsciiDigit(formatted[position])) beforeCaret--;
            position++;
        }
        box.CaretIndex = position;
    }
}
