using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// CORS (Unity calls this from device/pc)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddHttpClient();

var app = builder.Build();
app.UseCors();

// Health check
app.MapGet("/", () => Results.Text("OK"));

// POST /chat
app.MapPost("/chat", async (ChatRequest req, IHttpClientFactory httpFactory) =>
{
    var text = (req.Text ?? "").Trim();
    var npcId = string.IsNullOrWhiteSpace(req.NpcId) ? "idol_ashley" : req.NpcId.Trim();

    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "Missing text" });

    var supabaseUrl = Environment.GetEnvironmentVariable("SUPABASE_URL");
    var supabaseKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
    var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(supabaseKey))
        return Results.Problem("Missing Supabase env vars");

    if (string.IsNullOrWhiteSpace(openAiKey))
        return Results.Problem("Missing OpenAI env var");

    var http = httpFactory.CreateClient();

    // ---- Load persona ----
    var personaUrl =
        $"{supabaseUrl}/rest/v1/npc_personas?id=eq.{Uri.EscapeDataString(npcId)}&select=*";

    using var personaReq = new HttpRequestMessage(HttpMethod.Get, personaUrl);
    personaReq.Headers.Add("apikey", supabaseKey);
    personaReq.Headers.Add("Authorization", $"Bearer {supabaseKey}");

    var personaResp = await http.SendAsync(personaReq);
    if (!personaResp.IsSuccessStatusCode)
        return Results.NotFound(new { error = $"NPC not found: {npcId}" });

    var personaJson = await personaResp.Content.ReadAsStringAsync();
    var personas = JsonSerializer.Deserialize<List<NpcPersona>>(personaJson, Json.Options) ?? new();
    var persona = personas.Count > 0 ? personas[0] : null;

    if (persona == null)
        return Results.NotFound(new { error = $"NPC not found: {npcId}" });

    // ---- Load contexts (nodes) ----
    // We only load enabled contexts for this npc.
    // NOTE: "enabled" null won't match eq.true; make enabled default true in DB.
    var ctxUrl =
        $"{supabaseUrl}/rest/v1/npc_contexts?npc_id=eq.{Uri.EscapeDataString(npcId)}&enabled=eq.true&select=id,trigger_keywords,context_prompt,priority,enabled";

    using var ctxReq = new HttpRequestMessage(HttpMethod.Get, ctxUrl);
    ctxReq.Headers.Add("apikey", supabaseKey);
    ctxReq.Headers.Add("Authorization", $"Bearer {supabaseKey}");

    var ctxResp = await http.SendAsync(ctxReq);
    if (!ctxResp.IsSuccessStatusCode)
    {
        var body = await ctxResp.Content.ReadAsStringAsync();
        Console.WriteLine("Context fetch failed: " + body);
        return Results.Problem("Failed to load NPC contexts");
    }

    var ctxJson = await ctxResp.Content.ReadAsStringAsync();
    var contexts = JsonSerializer.Deserialize<List<NpcContext>>(ctxJson, Json.Options) ?? new();

    // ---- Node state in (from Unity) ----
    var state = req.State ?? new NodeState();
    // Defensive null handling
    state.Entered ??= new List<BoolFlag>();

    // Find the best matching context by keywords
    var userNorm = Helpers.NormalizeText(text);

    // match = all keywords must be present (v1 deterministic)
    NpcContext? bestMatch = null;
    foreach (var c in contexts)
    {
        if (c == null) continue;
        if (string.IsNullOrWhiteSpace(c.Id)) continue;
        if (string.IsNullOrWhiteSpace(c.ContextPrompt)) continue;

        if (!Helpers.MatchAny(userNorm, c.TriggerKeywords))
            continue;

        if (bestMatch == null)
        {
            bestMatch = c;
        }
        else
        {
            var pA = bestMatch.Priority ?? 0;
            var pB = c.Priority ?? 0;
            if (pB > pA)
                bestMatch = c;
        }
    }

    var enteredThisTurn = new List<string>(2);

    if (bestMatch != null)
    {
        // set active
        state.ActiveContext = bestMatch.Id;

        // mark entered if not already
        if (!Helpers.StateHasEntered(state, bestMatch.Id))
        {
            Helpers.SetEntered(state, bestMatch.Id, true);
            enteredThisTurn.Add(bestMatch.Id);
        }
    }

    // ---- Clamp history to last 20 messages ----
    var clippedHistory = (req.History ?? new List<ChatMessage>())
        .TakeLast(20)
        .Select(m => new OpenAiMessage(m.Role, m.Content ?? ""))
        .ToList();

    // ---- Build system prompt (persona + active context injection) ----
    var systemPrompt = persona.SystemPrompt ?? "You are an NPC.";

    var activeRow = !string.IsNullOrWhiteSpace(state.ActiveContext)
        ? contexts.FirstOrDefault(c => c.Id == state.ActiveContext)
        : null;

    if (activeRow != null && !string.IsNullOrWhiteSpace(activeRow.ContextPrompt))
    {
        systemPrompt += "\n\n=== CURRENT CONTEXT (focus) ===\n"
                     + activeRow.ContextPrompt.Trim()
                     + "\n=== END CONTEXT ===";
    }

    // ---- OpenAI request (Responses API) ----
    var input = new List<OpenAiMessage>
    {
        new("system", systemPrompt)
    };
    input.AddRange(clippedHistory);
    input.Add(new("user", text));

    var openAiPayload = new OpenAiResponsesRequest
    {
        Model = "gpt-4o-mini",
        Input = input,
        Temperature = persona.Temperature ?? 0.7,
        MaxOutputTokens = persona.MaxOutputTokens ?? 150
    };

    using var openAiReq = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
    openAiReq.Headers.Add("Authorization", $"Bearer {openAiKey}");
    openAiReq.Content = new StringContent(
        JsonSerializer.Serialize(openAiPayload, Json.Options),
        Encoding.UTF8,
        "application/json"
    );

    var openAiResp = await http.SendAsync(openAiReq);
    if (!openAiResp.IsSuccessStatusCode)
    {
        var errBody = await openAiResp.Content.ReadAsStringAsync();
        Console.WriteLine(errBody);
        return Results.Problem("AI request failed");
    }

    var openAiJson = await openAiResp.Content.ReadAsStringAsync();
    Console.WriteLine(openAiJson);

    var openAi = JsonSerializer.Deserialize<OpenAiResponsesResponse>(openAiJson, Json.Options);

    var reply =
        openAi?.OutputText
        ?? openAi?.Output?.FirstOrDefault()?.Content?.FirstOrDefault()?.Text
        ?? "No response";

    // ---- Response shape matches Unity JsonUtility ----
    return Results.Json(new ChatResponse
    {
        Line = reply,
        Npc = persona.Name,
        NpcId = persona.Id,
        EnteredContexts = enteredThisTurn,
        ActiveContext = state.ActiveContext,
        State = state
    });
});

app.Run();


// ---------- Models ----------

public record ChatRequest(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("npcId")] string? NpcId,
    [property: JsonPropertyName("history")] List<ChatMessage>? History,
    [property: JsonPropertyName("state")] NodeState? State
);

public record ChatResponse
{
    [JsonPropertyName("line")] public string Line { get; set; } = "";
    [JsonPropertyName("npc")] public string Npc { get; set; } = "";
    [JsonPropertyName("npcId")] public string NpcId { get; set; } = "";

    // Node info (v1)
    [JsonPropertyName("enteredContexts")] public List<string>? EnteredContexts { get; set; }
    [JsonPropertyName("activeContext")] public string? ActiveContext { get; set; }
    [JsonPropertyName("state")] public NodeState? State { get; set; }
}

public record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content
);

public record NpcPersona
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("system_prompt")] public string? SystemPrompt { get; set; }
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("max_output_tokens")] public int? MaxOutputTokens { get; set; }
}

public record NpcContext
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("trigger_keywords")] public string? TriggerKeywords { get; set; } // CSV
    [JsonPropertyName("context_prompt")] public string? ContextPrompt { get; set; }
    [JsonPropertyName("priority")] public int? Priority { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
}

public record BoolFlag
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("value")] public bool Value { get; set; }
}

public record NodeState
{
    [JsonPropertyName("entered")] public List<BoolFlag>? Entered { get; set; } = new();
    [JsonPropertyName("activeContext")] public string? ActiveContext { get; set; }
}

// OpenAI request/response minimal models
public record OpenAiMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content
);

public record OpenAiResponsesRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("input")] public List<OpenAiMessage> Input { get; set; } = new();
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
    [JsonPropertyName("max_output_tokens")] public int MaxOutputTokens { get; set; }
}

public record OpenAiResponsesResponse
{
    [JsonPropertyName("output_text")] public string? OutputText { get; set; }
    [JsonPropertyName("output")] public List<OpenAiOutputItem>? Output { get; set; }
}

public record OpenAiOutputItem
{
    [JsonPropertyName("content")] public List<OpenAiContentItem>? Content { get; set; }
}

public record OpenAiContentItem
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public static class Helpers
{
    public static string NormalizeText(string s)
    {
        var lower = (s ?? "").ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);

        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                sb.Append(ch);
            else
                sb.Append(' ');
        }

        return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool MatchAny(string userNorm, string? rawKeywordsCsv)
    {
        var keys = ParseCsvKeywords(rawKeywordsCsv);
        if (keys.Count == 0) return false;

        foreach (var k in keys)
        {
            if (k.Length < 3) continue;

            if (userNorm == k ||
                userNorm.StartsWith(k + " ") ||
                userNorm.EndsWith(" " + k) ||
                userNorm.Contains(" " + k + " "))
            {
                return true;
            }
        }

        return false;
    }

    public static List<string> ParseCsvKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();
        return raw.Split(',')
                  .Select(k => k.Trim().ToLowerInvariant())
                  .Where(k => !string.IsNullOrWhiteSpace(k))
                  .ToList();
    }

    public static bool StateHasEntered(NodeState state, string id)
    {
        var list = state.Entered;
        if (list == null) return false;

        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id)
                return list[i].Value;

        return false;
    }

    public static void SetEntered(NodeState state, string id, bool value)
    {
        state.Entered ??= new List<BoolFlag>();

        for (int i = 0; i < state.Entered.Count; i++)
        {
            if (state.Entered[i].Id == id)
            {
                state.Entered[i].Value = value;
                return;
            }
        }

        state.Entered.Add(new BoolFlag { Id = id, Value = value });
    }
}


 
