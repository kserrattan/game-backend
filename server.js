import express from "express";
import cors from "cors";
import { createClient } from "@supabase/supabase-js";

const app = express();
app.use(cors());
app.use(express.json());

const supabase = createClient(
    process.env.SUPABASE_URL,
    process.env.SUPABASE_SERVICE_ROLE_KEY
);

app.get("/", (req, res) => res.send("OK"));

app.post("/chat", async (req, res) => {
    const text = String(req.body?.text ?? "").trim();
    if (!text) return res.status(400).json({ error: "Missing text" });

    // quick DB write to prove Supabase works
    await supabase.from("ai_usage").upsert(
        { firebase_uid: "test-user", day: new Date().toISOString().slice(0, 10), requests: 1 },
        { onConflict: "firebase_uid,day" }
    );

    res.json({ line: "Backend is live ", echo: text });
});

const port = process.env.PORT || 3000;
app.listen(port, () => console.log("Server listening on", port));