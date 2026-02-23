import express from "express";
import cors from "cors";
import { createClient } from "@supabase/supabase-js";
import OpenAI from "openai";

const app = express();
app.use(cors());
app.use(express.json());

// Supabase (already working for you)
const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_SERVICE_ROLE_KEY
);

// OpenAI (SECURE - server only)
const openai = new OpenAI({
  apiKey: process.env.OPENAI_API_KEY,
});

// Health check (Render uses this)
app.get("/", (req, res) => {
  res.send("OK");
});

// Chat endpoint (Unity will call this)
app.post("/chat", async (req, res) => {
  try {
    const text = String(req.body?.text ?? "").trim();
    if (!text) {
      return res.status(400).json({ error: "Missing text" });
    }

    // Optional: track usage in Supabase (you already set this up)
    await supabase.from("ai_usage").upsert(
      {
        firebase_uid: "test-user",
        day: new Date().toISOString().slice(0, 10),
        requests: 1,
      },
      { onConflict: "firebase_uid,day" }
    );

    // REAL AI CALL (instead of echo)
    const response = await openai.responses.create({
      model: "gpt-4o-mini",
      input: [
        {
          role: "system",
          content:
            "You are an NPC in a game. Speak briefly and stay in character.",
        },
        {
          role: "user",
          content: text,
        },
      ],
    });

    // Safest way to extract text
    const reply =
      response.output_text ||
      response.output?.[0]?.content?.[0]?.text ||
      "No response";

    res.json({ line: reply });
  } catch (err) {
    console.error("Chat error:", err);
    res.status(500).json({ error: "AI request failed" });
  }
});

// Render provides PORT automatically
const port = process.env.PORT || 3000;
app.listen(port, () => {
  console.log("Server listening on", port);
});