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

    // 1) Fetch persona from Supabase REST
    // Supabase PostgREST: /rest/v1/npc_personas?id=eq.<npcId>&select=*
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

    // 2) Clamp history to last 20 messages
    var clippedHistory = (req.History ?? new List<ChatMessage>())
        .TakeLast(20)
        .Select(m => new OpenAiMessage(m.Role, m.Content ?? ""))
        .ToList();

    // 3) Build OpenAI request (Responses API)
    var input = new List<OpenAiMessage>
    {
        new("system", persona.SystemPrompt ?? "You are an NPC.")
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
    openAiReq.Content = new StringContent(JsonSerializer.Serialize(openAiPayload, Json.Options), Encoding.UTF8, "application/json");

    var openAiResp = await http.SendAsync(openAiReq);
    if (!openAiResp.IsSuccessStatusCode)
    {
        var errBody = await openAiResp.Content.ReadAsStringAsync();
        Console.WriteLine(errBody);
        return Results.Problem("AI request failed");
    }

    var openAiJson = await openAiResp.Content.ReadAsStringAsync();
    var openAi = JsonSerializer.Deserialize<OpenAiResponsesResponse>(openAiJson, Json.Options);

    var reply = openAi?.OutputText ?? "No response";

    return Results.Json(new
    {
        line = reply,
        npc = persona.Name,
        npcId = persona.Id
    });
});

app.Run();


// ---------- Models ----------

public record ChatRequest(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("npcId")] string? NpcId,
    [property: JsonPropertyName("history")] List<ChatMessage>? History
);

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
    // Responses API often includes "output_text"
    [JsonPropertyName("output_text")] public string? OutputText { get; set; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}