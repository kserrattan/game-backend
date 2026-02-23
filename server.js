import express from "express";
import cors from "cors";
import { createClient } from "@supabase/supabase-js";
import OpenAI from "openai";

const app = express();
app.use(cors());
app.use(express.json());

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_SERVICE_ROLE_KEY
);

const openai = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });

app.get("/", (req, res) => res.send("OK"));

app.post("/chat", async (req, res) => {
  try {
    const text = String(req.body?.text ?? "").trim();
    if (!text) return res.status(400).json({ error: "Missing text" });

    // (optional) prove Supabase works
    await supabase.from("ai_usage").upsert(
      { firebase_uid: "test-user", day: new Date().toISOString().slice(0, 10), requests: 1 },
      { onConflict: "firebase_uid,day" }
    );

    // OpenAI call
    const resp = await openai.responses.create({
      model: "gpt-4o-mini",
      input: [
        { role: "system", content: "You are an NPC in a game. Reply in 1-2 short sentences." },
        { role: "user", content: text }
      ],
    });

    const reply = resp.output_text ?? "(no reply)";
    return res.json({ reply });
  } catch (err) {
    console.error(err);
    return res.status(500).json({ error: "Server error" });
  }
});

const port = process.env.PORT || 3000;
app.listen(port, () => console.log("Server listening on", port));
