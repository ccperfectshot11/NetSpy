using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace NetSpy.Designer.Model
{
	/// <summary>O setare de design. Valoarea e ținută ca text (ex. "#FF0067C0", "8", "true", "Rounded"),
	/// ca să poată fi salvată direct în JSON.</summary>
	public abstract class Setting : INotifyPropertyChanged
	{
		string value;

		protected Setting(string key, string label, string group, string defaultValue, string? hint)
		{
			Key = key;
			Label = label;
			Group = group;
			DefaultValue = defaultValue;
			Hint = hint;
			value = defaultValue;
		}

		public string Key { get; }
		public string Label { get; }
		public string Group { get; }
		public string DefaultValue { get; }
		public string? Hint { get; }

		public string Value
		{
			get => value;
			set
			{
				var normalized = Normalize(value);
				if (normalized == this.value)
					return;
				this.value = normalized;
				OnPropertyChanged();
				OnValueChanged();
			}
		}

		protected virtual string Normalize(string value) => value;
		protected virtual void OnValueChanged() { }

		public event PropertyChangedEventHandler? PropertyChanged;

		protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}

	public sealed class ColorSetting : Setting
	{
		public ColorSetting(string key, string label, string group, string defaultValue, string? hint = null)
			: base(key, label, group, defaultValue, hint) { }

		protected override string Normalize(string value) =>
			ColorUtil.TryParse(value, out var color) ? ColorUtil.ToHex(color) : Value;
	}

	public sealed class NumberSetting : Setting
	{
		public NumberSetting(string key, string label, string group, double defaultValue, double min, double max, string unit = "px", string? hint = null)
			: base(key, label, group, defaultValue.ToString(CultureInfo.InvariantCulture), hint)
		{
			Min = min;
			Max = max;
			Unit = unit;
		}

		public double Min { get; }
		public double Max { get; }
		public string Unit { get; }

		/// <summary>Pentru Slider (legat two-way).</summary>
		public double Number
		{
			get => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
			set => Value = Math.Round(Math.Clamp(value, Min, Max)).ToString(CultureInfo.InvariantCulture);
		}

		protected override string Normalize(string value) =>
			double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
				? Math.Round(Math.Clamp(d, Min, Max)).ToString(CultureInfo.InvariantCulture)
				: Value;

		protected override void OnValueChanged() => OnPropertyChanged(nameof(Number));
	}

	public sealed class BoolSetting : Setting
	{
		public BoolSetting(string key, string label, string group, bool defaultValue, string? hint = null)
			: base(key, label, group, defaultValue ? "true" : "false", hint) { }

		public bool IsChecked
		{
			get => Value == "true";
			set => Value = value ? "true" : "false";
		}

		protected override string Normalize(string value) =>
			bool.TryParse(value, out var b) ? (b ? "true" : "false") : Value;

		protected override void OnValueChanged() => OnPropertyChanged(nameof(IsChecked));
	}

	public sealed record Choice(string Id, string Label)
	{
		// ComboBox-ul afișează obiectul prin ToString()
		public override string ToString() => Label;
	}

	public sealed class ChoiceSetting : Setting
	{
		public ChoiceSetting(string key, string label, string group, IReadOnlyList<Choice> choices, string defaultId, string? hint = null)
			: base(key, label, group, defaultId, hint)
		{
			Choices = choices;
		}

		public IReadOnlyList<Choice> Choices { get; }

		public Choice? Selected
		{
			get => Choices.FirstOrDefault(c => c.Id == Value);
			set { if (value is not null) Value = value.Id; }
		}

		protected override string Normalize(string value) =>
			Choices.Any(c => c.Id == value) ? value : Value;

		protected override void OnValueChanged() => OnPropertyChanged(nameof(Selected));
	}

	public sealed class FontSetting : Setting
	{
		public FontSetting(string key, string label, string group, string defaultValue, string? hint = null)
			: base(key, label, group, defaultValue, hint) { }

		public static IReadOnlyList<string> AllFonts { get; } =
			System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

		protected override string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? Value : value.Trim();
	}

	public sealed class SettingGroup
	{
		public SettingGroup(string name, IReadOnlyList<Setting> items, bool isExpanded)
		{
			Name = name;
			Items = items;
			IsExpanded = isExpanded;
		}

		public string Name { get; }
		public IReadOnlyList<Setting> Items { get; }
		public bool IsExpanded { get; set; }
	}
}
