using System.Text;
using System.Text.Json;

namespace MapRanks;

public static class AutoDiagnosticText
{
	public static string Render(string json)
	{
		using (JsonDocument jsonDocument = JsonDocument.Parse(json))
		{
			JsonElement root = jsonDocument.RootElement;
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("MapRanks automatic status + dump (read-only)");
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(21, 3, stringBuilder2);
			handler.AppendLiteral("version=");
			handler.AppendFormatted(V(new string[1] { "version" }));
			handler.AppendLiteral(" utc=");
			handler.AppendFormatted(V(new string[1] { "utc" }));
			handler.AppendLiteral(" reason=");
			handler.AppendFormatted(V(new string[1] { "reason" }));
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(35, 5, stringBuilder2);
			handler.AppendLiteral("map=");
			handler.AppendFormatted(V(new string[1] { "map" }));
			handler.AppendLiteral(" epoch=");
			handler.AppendFormatted(V(new string[1] { "epoch" }));
			handler.AppendLiteral(" active=");
			handler.AppendFormatted(V(new string[1] { "active" }));
			handler.AppendLiteral(" phase=");
			handler.AppendFormatted(V(new string[1] { "phase" }));
			handler.AppendLiteral(" blocked=");
			handler.AppendFormatted(V(new string[1] { "blocked" }));
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(23, 3, stringBuilder2);
			handler.AppendLiteral("freeze=");
			handler.AppendFormatted(V(new string[2] { "rules", "freeze" }));
			handler.AppendLiteral(" warmup=");
			handler.AppendFormatted(V(new string[2] { "rules", "warmup" }));
			handler.AppendLiteral(" rounds=");
			handler.AppendFormatted(V(new string[2] { "rules", "rounds" }));
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(33, 2, stringBuilder2);
			handler.AppendLiteral("active_assigned=");
			handler.AppendFormatted(V(new string[2] { "counters", "active_assigned" }));
			handler.AppendLiteral(" map_assignments=");
			handler.AppendFormatted(V(new string[2] { "counters", "assigned" }));
			stringBuilder6.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(38, 3, stringBuilder2);
			handler.AppendLiteral("wire_batches=");
			handler.AppendFormatted(V(new string[2] { "counters", "wire_batches" }));
			handler.AppendLiteral(" wire_rows=");
			handler.AppendFormatted(V(new string[2] { "counters", "wire_rows" }));
			handler.AppendLiteral(" reveals_sent=");
			handler.AppendFormatted(V(new string[2] { "counters", "reveals_sent" }));
			stringBuilder7.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(50, 3, stringBuilder2);
			handler.AppendLiteral("own_update_pre=");
			handler.AppendFormatted(V(new string[2] { "publication", "own_update_pre" }));
			handler.AppendLiteral(" own_reveal_pre=");
			handler.AppendFormatted(V(new string[2] { "publication", "own_reveal_pre" }));
			handler.AppendLiteral(" packet_mismatches=");
			handler.AppendFormatted(V(new string[2] { "publication", "packet_mismatches" }));
			stringBuilder8.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder9 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(48, 4, stringBuilder2);
			handler.AppendLiteral("generation=");
			handler.AppendFormatted(V(new string[3] { "first_live", "gate", "generation" }));
			handler.AppendLiteral(" state=");
			handler.AppendFormatted(V(new string[3] { "first_live", "gate", "state" }));
			handler.AppendLiteral(" live_queued=");
			handler.AppendFormatted(V(new string[2] { "first_live", "queued" }));
			handler.AppendLiteral(" live_dispatched=");
			handler.AppendFormatted(V(new string[2] { "first_live", "completed" }));
			stringBuilder9.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder10 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(55, 3, stringBuilder2);
			handler.AppendLiteral("reset_freeze_enabled=");
			handler.AppendFormatted(V(new string[2] { "boundary_visibility", "reset_freeze_enabled" }));
			handler.AppendLiteral(" freeze_queued=");
			handler.AppendFormatted(V(new string[2] { "boundary_visibility", "freeze_queued" }));
			handler.AppendLiteral(" freeze_dispatched=");
			handler.AppendFormatted(V(new string[2] { "boundary_visibility", "freeze_dispatched" }));
			stringBuilder10.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder11 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(36, 2, stringBuilder2);
			handler.AppendLiteral("next_match_pending=");
			handler.AppendFormatted(V(new string[3] { "boundary_visibility", "match_signals", "awaiting_next_match" }));
			handler.AppendLiteral(" identity_writes=");
			handler.AppendFormatted(V(new string[2] { "counters", "identity_writes" }));
			stringBuilder11.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder12 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(32, 2, stringBuilder2);
			handler.AppendLiteral("wire_error=");
			handler.AppendFormatted(V(new string[2] { "wire", "Error" }));
			handler.AppendLiteral(" repair_drift_writes=");
			handler.AppendFormatted(V(new string[2] { "field_repairs", "drift_writes" }));
			stringBuilder12.AppendLine(ref handler);
			stringBuilder.AppendLine("UI=UNKNOWN. Server dispatch and Pre observation are NOT client acknowledgement.");
			stringBuilder.AppendLine("PLAYERS (same snapshot, no allocation or refresh):");
			JsonElement jsonElement = At(root, new string[1] { "players" });
			if (jsonElement.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in jsonElement.EnumerateArray())
				{
					JsonElement p = item;
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder13 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(66, 8, stringBuilder2);
					handler.AppendLiteral("slot=");
					handler.AppendFormatted(P(new string[1] { "slot" }));
					handler.AppendLiteral(" bot=");
					handler.AppendFormatted(P(new string[1] { "bot" }));
					handler.AppendLiteral(" assigned=");
					handler.AppendFormatted(P(new string[2] { "assigned", "Rating" }));
					handler.AppendLiteral(" actual=");
					handler.AppendFormatted(P(new string[2] { "actual", "rating" }));
					handler.AppendLiteral(" type=");
					handler.AppendFormatted(P(new string[2] { "actual", "rank_type" }));
					handler.AppendLiteral(" team=");
					handler.AppendFormatted(P(new string[1] { "team" }));
					handler.AppendLiteral(" identity_matches=");
					handler.AppendFormatted(P(new string[1] { "identity_matches_controller" }));
					handler.AppendLiteral(" errors=");
					handler.AppendFormatted(P(new string[1] { "errors" }));
					stringBuilder13.AppendLine(ref handler);
					string P(params string[] path)
					{
						return Text(At(p, path));
					}
				}
			}
			stringBuilder.AppendLine("No console screenshots are required. After playing run 02_COLLECT_RETURN.cmd.");
			return stringBuilder.ToString();
			string V(params string[] path)
			{
				return Text(At(root, path));
			}
		}
		static JsonElement At(JsonElement value, params string[] path)
		{
			foreach (string propertyName in path)
			{
				if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(propertyName, out value))
				{
					return default;
				}
			}
			return value;
		}
		static string Text(JsonElement value)
		{
			JsonValueKind valueKind = value.ValueKind;
			if ((valueKind != JsonValueKind.Undefined && valueKind != JsonValueKind.Null) || 1 == 0)
			{
				return value.ToString();
			}
			return "n/a";
		}
	}
}
