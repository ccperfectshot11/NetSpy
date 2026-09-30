using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NetSpy.Designer.Preview
{
	/// <summary>O bucată de cod colorată: textul și cheia culorii ("SyntaxKeyword"...; null = textul normal).</summary>
	public readonly record struct Token(string Text, string? Color);

	public sealed record CodeSample(string Title, string Icon, string Code);

	/// <summary>Exemple de cod decompilat și un colorizator minimal (doar pentru previzualizare).</summary>
	public static class CodeSamples
	{
		public static IReadOnlyList<CodeSample> All { get; } = new[] {
			new CodeSample("JsonConvert", "Class", """
				// Decompilat cu NetSpy (motor ILSpy)
				using System;
				using System.Globalization;

				namespace Newtonsoft.Json;

				public static class JsonConvert
				{
				    public static readonly string True = "true";

				    public static string SerializeObject(object? value, Formatting formatting)
				    {
				        var settings = new JsonSerializerSettings { Formatting = formatting };
				        if (value is null)
				        {
				            return "null";
				        }
				        int capacity = 256;
				        return SerializeObjectInternal(value, null, settings, capacity);
				    }

				    public static string ToString(DateTime value) => ToString(value, DateFormatHandling.IsoDateFormat);
				}
				"""),
			new CodeSample("JsonToken", "Enum", """
				namespace Newtonsoft.Json;

				public enum JsonToken
				{
				    None,
				    StartObject,
				    StartArray,
				    PropertyName = 4,
				    Comment,
				    Integer = 7,
				    Float,
				    String
				}

				internal static class JsonTokenUtils
				{
				    // expresie switch reconstruită din IL
				    public static bool IsPrimitiveToken(JsonToken token) => token switch
				    {
				        JsonToken.Integer or JsonToken.Float or JsonToken.String => true,
				        _ => false
				    };
				}
				"""),
			new CodeSample("Program", "Method", """
				using System;
				using Newtonsoft.Json;

				var person = new Person("Ana", 31);
				string json = JsonConvert.SerializeObject(person);
				Console.WriteLine(json);

				foreach (var (key, value) in Load("config.json"))
				{
				    Console.WriteLine($"{key} = {value}");
				}

				public record Person(string Name, int Age);
				"""),
		};

		static readonly HashSet<string> Keywords = new() {
			"using", "namespace", "public", "private", "internal", "protected", "static", "readonly", "class", "record",
			"struct", "enum", "interface", "new", "null", "true", "false", "is", "or", "and", "not", "var", "string",
			"object", "int", "bool", "void", "this", "base", "sealed", "abstract", "override", "virtual", "const",
			"typeof", "nameof", "default", "out", "ref", "in", "get", "set", "init",
		};

		static readonly HashSet<string> ControlKeywords = new() {
			"if", "else", "return", "switch", "case", "break", "continue", "for", "foreach", "while", "do",
			"throw", "try", "catch", "finally", "yield", "await", "goto",
		};

		static readonly HashSet<string> Types = new() {
			"JsonConvert", "JsonSerializerSettings", "Formatting", "DateTime", "DateFormatHandling", "JsonToken",
			"JsonTokenUtils", "Console", "Person", "Globalization", "System", "Newtonsoft", "Json",
		};

		static readonly HashSet<string> Locals = new() {
			"value", "formatting", "settings", "capacity", "token", "person", "json", "key",
		};

		static readonly Regex Lexer = new(
			@"(?<comment>//[^\n]*)|(?<string>\$?""[^""\n]*"")|(?<number>\b\d+(\.\d+)?\b)|(?<word>[A-Za-z_][A-Za-z0-9_]*)|(?<other>.)",
			RegexOptions.Compiled | RegexOptions.Singleline);

		/// <summary>Împarte codul în linii de tokenuri colorate.</summary>
		public static List<List<Token>> Colorize(string code)
		{
			var lines = new List<List<Token>>();
			foreach (var line in code.Replace("\r\n", "\n").Split('\n'))
			{
				var tokens = new List<Token>();
				var matches = Lexer.Matches(line);
				for (int i = 0; i < matches.Count; i++)
				{
					var m = matches[i];
					string? color = null;
					if (m.Groups["comment"].Success)
						color = "SyntaxComment";
					else if (m.Groups["string"].Success)
						color = "SyntaxString";
					else if (m.Groups["number"].Success)
						color = "SyntaxNumber";
					else if (m.Groups["word"].Success)
					{
						var word = m.Value;
						bool call = NextNonSpace(line, m.Index + m.Length) == '(';
						if (ControlKeywords.Contains(word))
							color = "SyntaxControl";
						else if (Keywords.Contains(word))
							color = "SyntaxKeyword";
						else if (Types.Contains(word))
							color = "SyntaxType";
						else if (call)
							color = "SyntaxMethod";
						else if (Locals.Contains(word))
							color = "SyntaxLocal";
					}
					// tokenurile vecine cu aceeași culoare se unesc (mai puține elemente WPF)
					if (tokens.Count > 0 && tokens[^1].Color == color)
						tokens[^1] = new Token(tokens[^1].Text + m.Value, color);
					else
						tokens.Add(new Token(m.Value, color));
				}
				lines.Add(tokens);
			}
			return lines;
		}

		static char NextNonSpace(string line, int index)
		{
			while (index < line.Length && line[index] == ' ')
				index++;
			return index < line.Length ? line[index] : '\0';
		}
	}
}
