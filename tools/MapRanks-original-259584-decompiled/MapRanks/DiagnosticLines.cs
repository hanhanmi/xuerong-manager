using System;
using System.Collections.Generic;
using System.Text;

namespace MapRanks;

public static class DiagnosticLines
{
	public static string[] Split(string text, int maxBytes = 180)
	{
		if (maxBytes < 4 || maxBytes > 512)
		{
			throw new ArgumentOutOfRangeException("maxBytes");
		}
		List<string> list = new List<string>();
		StringBuilder stringBuilder = new StringBuilder();
		int num = 0;
		foreach (Rune item in text.EnumerateRunes())
		{
			if (item.Value == 13)
			{
				continue;
			}
			if (item.Value == 10)
			{
				list.Add(stringBuilder.ToString());
				stringBuilder.Clear();
				num = 0;
				continue;
			}
			int utf8SequenceLength = item.Utf8SequenceLength;
			if (num + utf8SequenceLength > maxBytes)
			{
				list.Add(stringBuilder.ToString());
				stringBuilder.Clear();
				num = 0;
			}
			stringBuilder.Append(item.ToString());
			num += utf8SequenceLength;
		}
		if (stringBuilder.Length > 0 || list.Count == 0)
		{
			list.Add(stringBuilder.ToString());
		}
		return list.ToArray();
	}
}
