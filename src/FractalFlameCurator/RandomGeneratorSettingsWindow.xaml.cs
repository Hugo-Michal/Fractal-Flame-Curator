using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Models;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace FractalFlameCurator;

public partial class RandomGeneratorSettingsWindow : Window
{
    private readonly List<VariationChoice> _variationChoices;
    private readonly ICollectionView _variationView;

    public RandomGeneratorSettings AppliedSettings { get; private set; }

    public RandomGeneratorSettingsWindow(RandomGeneratorSettings currentSettings)
    {
        InitializeComponent();
        AppliedSettings = currentSettings.Snapshot();
        _variationChoices = VariationRegistry.All
            .Select(definition => new VariationChoice(definition.Name, definition.Category))
            .ToList();
        VariationListBox.ItemsSource = _variationChoices;
        _variationView = CollectionViewSource.GetDefaultView(_variationChoices);
        _variationView.Filter = MatchesVariationSearch;
        LoadSettings(currentSettings);
    }

    private void LoadDefaults_Click(object sender, RoutedEventArgs e) => LoadSettings(RandomGeneratorSettings.CreateDefault());

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var symmetryTypes = AllowedSymmetryTypes.None;
            if (RotationalSymmetryCheckBox.IsChecked == true) symmetryTypes |= AllowedSymmetryTypes.Rotational;
            if (ReflectionSymmetryCheckBox.IsChecked == true) symmetryTypes |= AllowedSymmetryTypes.Reflection;
            if (DihedralSymmetryCheckBox.IsChecked == true) symmetryTypes |= AllowedSymmetryTypes.Dihedral;

            var settings = new RandomGeneratorSettings
            {
                MinimumTransformCount = ReadInt(MinimumTransformCountTextBox, "Minimum transform count", 2, 12),
                MaximumTransformCount = ReadInt(MaximumTransformCountTextBox, "Maximum transform count", 2, 12),
                SymmetryTypes = symmetryTypes,
                MinimumAffineRotationDegrees = ReadDouble(MinimumAffineRotationTextBox, "Minimum affine rotation", -180, 180),
                MaximumAffineRotationDegrees = ReadDouble(MaximumAffineRotationTextBox, "Maximum affine rotation", -180, 180),
                MinimumAffineScale = ReadDouble(MinimumAffineScaleTextBox, "Minimum affine scale", 0.10, 1.25),
                MaximumAffineScale = ReadDouble(MaximumAffineScaleTextBox, "Maximum affine scale", 0.10, 1.25),
                MinimumAffineShear = ReadDouble(MinimumAffineShearTextBox, "Minimum affine shear", -1, 1),
                MaximumAffineShear = ReadDouble(MaximumAffineShearTextBox, "Maximum affine shear", -1, 1),
                TranslationExtent = ReadDouble(TranslationExtentTextBox, "Translation extent", 0, 2),
                TransformSelectionBalance = TransformBalanceSlider.Value / 100,
                MinimumVariationCount = ReadInt(MinimumVariationCountTextBox, "Minimum variation count", 1, 5),
                MaximumVariationCount = ReadInt(MaximumVariationCountTextBox, "Maximum variation count", 1, 5),
                EnabledVariations = _variationChoices.Where(choice => choice.IsEnabled).Select(choice => choice.Name).ToArray(),
                MinimumVariationShare = ReadDouble(MinimumVariationShareTextBox, "Minimum variation share", 0, 1),
                PostTransformChance = PostTransformChanceSlider.Value / 100,
                MinimumPostRotationDegrees = ReadDouble(MinimumPostRotationTextBox, "Minimum post rotation", -180, 180),
                MaximumPostRotationDegrees = ReadDouble(MaximumPostRotationTextBox, "Maximum post rotation", -180, 180),
                MinimumPostScale = ReadDouble(MinimumPostScaleTextBox, "Minimum post scale", 0.25, 2),
                MaximumPostScale = ReadDouble(MaximumPostScaleTextBox, "Maximum post scale", 0.25, 2),
                PostTranslationExtent = ReadDouble(PostTranslationExtentTextBox, "Post translation extent", 0, 1),
                AllowFinalTransforms = AllowFinalTransformsCheckBox.IsChecked == true,
                FinalTransformChance = FinalTransformChanceSlider.Value / 100
            };

            AppliedSettings = settings.Snapshot();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException)
        {
            WpfMessageBox.Show(this, exception.Message, "Invalid random generator settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void VariationSearch_TextChanged(object sender, TextChangedEventArgs e) => _variationView?.Refresh();

    private void SelectAllVariations_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _variationChoices) choice.IsEnabled = true;
    }

    private void ClearVariations_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _variationChoices) choice.IsEnabled = false;
    }

    private bool MatchesVariationSearch(object item)
    {
        if (item is not VariationChoice choice) return false;
        var search = VariationSearchTextBox.Text.Trim();
        return search.Length == 0 ||
            choice.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            choice.Category.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadSettings(RandomGeneratorSettings settings)
    {
        var snapshot = settings.Snapshot();
        MinimumTransformCountTextBox.Text = Format(snapshot.MinimumTransformCount);
        MaximumTransformCountTextBox.Text = Format(snapshot.MaximumTransformCount);
        RotationalSymmetryCheckBox.IsChecked = snapshot.SymmetryTypes.HasFlag(AllowedSymmetryTypes.Rotational);
        ReflectionSymmetryCheckBox.IsChecked = snapshot.SymmetryTypes.HasFlag(AllowedSymmetryTypes.Reflection);
        DihedralSymmetryCheckBox.IsChecked = snapshot.SymmetryTypes.HasFlag(AllowedSymmetryTypes.Dihedral);
        MinimumAffineRotationTextBox.Text = Format(snapshot.MinimumAffineRotationDegrees);
        MaximumAffineRotationTextBox.Text = Format(snapshot.MaximumAffineRotationDegrees);
        MinimumAffineScaleTextBox.Text = Format(snapshot.MinimumAffineScale);
        MaximumAffineScaleTextBox.Text = Format(snapshot.MaximumAffineScale);
        MinimumAffineShearTextBox.Text = Format(snapshot.MinimumAffineShear);
        MaximumAffineShearTextBox.Text = Format(snapshot.MaximumAffineShear);
        TranslationExtentTextBox.Text = Format(snapshot.TranslationExtent);
        TransformBalanceSlider.Value = snapshot.TransformSelectionBalance * 100;
        MinimumVariationCountTextBox.Text = Format(snapshot.MinimumVariationCount);
        MaximumVariationCountTextBox.Text = Format(snapshot.MaximumVariationCount);
        var enabled = snapshot.EnabledVariations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in _variationChoices) choice.IsEnabled = enabled.Contains(choice.Name);
        MinimumVariationShareTextBox.Text = Format(snapshot.MinimumVariationShare);
        PostTransformChanceSlider.Value = snapshot.PostTransformChance * 100;
        MinimumPostRotationTextBox.Text = Format(snapshot.MinimumPostRotationDegrees);
        MaximumPostRotationTextBox.Text = Format(snapshot.MaximumPostRotationDegrees);
        MinimumPostScaleTextBox.Text = Format(snapshot.MinimumPostScale);
        MaximumPostScaleTextBox.Text = Format(snapshot.MaximumPostScale);
        PostTranslationExtentTextBox.Text = Format(snapshot.PostTranslationExtent);
        AllowFinalTransformsCheckBox.IsChecked = snapshot.AllowFinalTransforms;
        FinalTransformChanceSlider.Value = snapshot.FinalTransformChance * 100;
        VariationSearchTextBox.Text = string.Empty;
    }

    private static int ReadInt(WpfTextBox textBox, string name, int minimum, int maximum)
    {
        if (!int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new FormatException($"{name} must be a whole number between {minimum} and {maximum}.");
        return value;
    }

    private static double ReadDouble(WpfTextBox textBox, string name, double minimum, double maximum)
    {
        if (!TryParseDouble(textBox.Text, out var value) || !double.IsFinite(value) || value < minimum || value > maximum)
            throw new FormatException($"{name} must be a number between {minimum} and {maximum}.");
        return value;
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class VariationChoice : INotifyPropertyChanged
    {
        private bool _isEnabled;

        public VariationChoice(string name, string category)
        {
            Name = name;
            Category = category;
        }

        public string Name { get; }
        public string Category { get; }
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
