// Local dictation server. Recognition is Qwen3-ASR on the GPU through llama.cpp's llama-server,
// started on the first key press and stopped again after cfg.asrIdleMinutes so VRAM is only held while in use.
// GET /start?device=<capture endpoint GUID> -> "REC" once audio is flowing, 500 with the ffmpeg error otherwise
// GET /stop   -> transcript; 422 "SILENT" when the mic delivered nothing but silence
// GET /cancel -> stop and discard
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { spawn, spawnSync } = require("node:child_process");
const { chineseToNum } = require("./chinese-itn");

const cfgPath = path.join(__dirname, "config.json");
const cfg = {
  // llama.cpp Vulkan build + ggml-org/Qwen3-ASR-1.7B-GGUF; config.json can move them anywhere.
  llamaServer: "D:/models/llama.cpp/bin/llama-server.exe",
  asrModel: "D:/models/qwen3-asr-1.7b/Qwen3-ASR-1.7B-Q8_0.gguf",
  asrMmproj: "D:/models/qwen3-asr-1.7b/mmproj-Qwen3-ASR-1.7B-Q8_0.gguf",
  asrPort: 8378,
  asrIdleMinutes: 10,
  ffmpeg: "ffmpeg",
  port: 8377,
  // Local Ollama model that tidies the raw transcript; "" turns the step off.
  polishModel: "",
  ollama: "http://127.0.0.1:11434",
  ...(fs.existsSync(cfgPath) ? JSON.parse(fs.readFileSync(cfgPath, "utf8")) : {}),
};

const SAMPLE_RATE = 16000;
const ASR_LOAD_TIMEOUT_MS = 60000;     // measured load is 3.6s; a cold disk can be much slower
const ASR_TIMEOUT_MS = 60000;
const SILENT_PEAK = 0.001;             // -60 dBFS; a muted or dead mic never gets near this
const START_TIMEOUT_MS = 5000;
const DSHOW_AUDIO_CACHE = "HKCU\\Software\\Microsoft\\ActiveMovie\\devenum 64-bit\\{33D9A762-90C8-11D0-BD43-00A0C911CE86}";
const GUID = /^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$/i;
const POLISH_TIMEOUT_MS = 10000;
const POLISH_PROMPT = `你是语音输入的文字整理器。输入是语音识别的原始结果，可能有：口吃重复、说到一半改口、"嗯""啊""那个""就是"之类的口头禅、同音字识别错误、标点缺失或断句不对。

把它整理成说话人本来想写的那句话：
- 删掉重复、口头禅、说错后改口的前半截，只保留改口后的说法
- 按上下文把同音错字改对
- 重新断句，补上正确的中文标点
- 不增加原文没有的意思，不回答、不评论、不解释
- 原文是提问就保留提问，是命令就保留命令
- 只输出整理后的文字`;

const asrUrl = "http://127.0.0.1:" + cfg.asrPort;
let asr = null;      // { proc, ready } while llama-server is running
let asrIdle = null;  // timer that stops it after cfg.asrIdleMinutes

let rec = null;   // recording in progress
let lastRms = 0;  // 0..1, most recent audio block

// A force-killed dictation server leaves its llama-server behind holding VRAM and the port.
function killStrayAsr() {
  spawnSync("powershell", ["-NoProfile", "-Command",
    `Get-CimInstance Win32_Process -Filter "Name='llama-server.exe'" | ? CommandLine -like '*--port ${cfg.asrPort}*' | % { Stop-Process -Id $_.ProcessId -Force }`,
  ], { windowsHide: true });
}

function ensureAsr() {
  if (asr) return asr.ready;
  const proc = spawn(cfg.llamaServer, [
    "-m", cfg.asrModel, "--mmproj", cfg.asrMmproj,
    "-ngl", "99", "-c", "4096",
    "--load-mode", "none",   // default mmap kept another ~0.8GB of the model mapped in RAM
    "--host", "127.0.0.1", "--port", String(cfg.asrPort),
  ], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
  const self = { proc };
  let log = "", gone = false;
  proc.stderr.on("data", (b) => { log = (log + b).slice(-4000); });
  const end = (why) => { gone = true; log += why || ""; if (asr === self) asr = null; };
  proc.once("exit", () => end());
  proc.once("error", (err) => end(err.message));
  self.ready = (async () => {
    const deadline = Date.now() + ASR_LOAD_TIMEOUT_MS;
    while (Date.now() < deadline) {
      if (gone) throw new Error("llama-server exited: " + log.trim().split("\n").slice(-3).join(" | "));
      try { if ((await fetch(asrUrl + "/health")).ok) return; } catch {}
      await new Promise((r) => setTimeout(r, 200));
    }
    proc.kill();
    throw new Error(`llama-server not ready within ${ASR_LOAD_TIMEOUT_MS}ms`);
  })();
  self.ready.catch(() => {});
  asr = self;
  return self.ready;
}

function touchAsr() {
  clearTimeout(asrIdle);
  asrIdle = setTimeout(() => {
    if (rec) return touchAsr();
    if (asr) asr.proc.kill();
  }, cfg.asrIdleMinutes * 60000);
}

function toWav(samples) {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write("RIFF", 0); b.writeUInt32LE(b.length - 8, 4); b.write("WAVE", 8);
  b.write("fmt ", 12); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22);
  b.writeUInt32LE(SAMPLE_RATE, 24); b.writeUInt32LE(SAMPLE_RATE * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34);
  b.write("data", 36); b.writeUInt32LE(samples.length * 2, 40);
  for (let i = 0; i < samples.length; i++) b.writeInt16LE(Math.round(Math.max(-1, Math.min(1, samples[i])) * 32767), 44 + i * 2);
  return b;
}

async function transcribe(samples) {
  await ensureAsr();
  const r = await fetch(asrUrl + "/v1/chat/completions", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    signal: AbortSignal.timeout(ASR_TIMEOUT_MS),
    body: JSON.stringify({
      temperature: 0,
      messages: [{ role: "user", content: [{ type: "input_audio", input_audio: { data: toWav(samples).toString("base64"), format: "wav" } }] }],
    }),
  });
  const j = await r.json();
  if (!r.ok) throw new Error((j.error && j.error.message) || "llama-server " + r.status);
  // Qwen3-ASR answers "language Chinese<asr_text>..."; llama.cpp passes the prefix through (issue #26749)
  const out = j.choices[0].message.content;
  const at = out.indexOf("<asr_text>");
  return normalizeNumbers((at >= 0 ? out.slice(at + "<asr_text>".length) : out).trim());
}

// Qwen3-ASR writes most numbers out in characters ("十六分钟"); SenseVoice used to emit digits.
// The client converts Traditional to Simplified only after this, so fold the Traditional forms the
// number rules look for first, or an occasional Traditional answer would keep its numbers spelled out.
const TRAD_NUMBER_CHARS = { "兩": "两", "點": "点", "萬": "万", "億": "亿", "個": "个", "隻": "只", "鐘": "钟", "號": "号", "塊": "块", "層": "层", "時": "时", "幾": "几" };
function normalizeNumbers(text) {
  return chineseToNum(text.replace(/[兩點萬億個隻鐘號塊層時幾]/g, (c) => TRAD_NUMBER_CHARS[c]));
}

function blockStats(buf, skip) {
  let sum = 0, peak = 0, n = 0;
  for (let i = skip; i + 4 <= buf.length; i += 4) {
    const v = Math.abs(buf.readFloatLE(i));
    sum += v * v;
    if (v > peak) peak = v;
    n += 1;
  }
  return { rms: n ? Math.sqrt(sum / n) : 0, peak };
}

function startRecording(guid) {
  return new Promise((resolve, reject) => {
    const proc = spawn(cfg.ffmpeg, [
      "-hide_banner", "-loglevel", "error",
      "-f", "dshow", "-audio_buffer_size", "50",
      // dshow's alternative name embeds the WASAPI endpoint GUID the client sends
      "-i", `audio=@device_cm_{33D9A762-90C8-11D0-BD43-00A0C911CE86}\\wave_{${guid.toUpperCase()}}`,
      "-ac", "1", "-ar", String(SAMPLE_RATE), "-f", "f32le", "-",
    ]);
    const r = { proc, chunks: [], bytes: 0, peak: 0, log: "" };
    r.closed = new Promise((done) => {
      proc.once("close", done);
      proc.once("error", (err) => { r.log += err.message; done(); });
    });
    let started = false;
    const timer = setTimeout(() => {
      r.log += `no audio within ${START_TIMEOUT_MS}ms`;
      proc.kill();
    }, START_TIMEOUT_MS);
    proc.stdin.on("error", () => {});
    proc.stderr.on("data", (b) => { r.log += b; });
    proc.stdout.on("data", (b) => {
      const s = blockStats(b, (4 - (r.bytes % 4)) % 4);
      r.chunks.push(b);
      r.bytes += b.length;
      if (s.peak > r.peak) r.peak = s.peak;
      if (!started) {
        started = true;
        clearTimeout(timer);
        rec = r;
        resolve();
      }
      if (rec === r) lastRms = s.rms;
    });
    r.closed.then(() => {
      clearTimeout(timer);
      if (!started) reject(new Error(r.log.trim() || "ffmpeg exited"));
    });
  });
}

async function stopRecording() {
  const r = rec;
  rec = null;
  lastRms = 0;
  if (!r) return null;
  // "q" lets ffmpeg flush its output buffer; a plain kill cut ~0.35s off the end in testing
  r.proc.stdin.write("q");
  const kill = setTimeout(() => r.proc.kill(), 2000);
  await r.closed;
  clearTimeout(kill);
  const raw = Buffer.concat(r.chunks);
  const usable = raw.length - (raw.length % 4);
  return {
    samples: new Float32Array(raw.buffer.slice(raw.byteOffset, raw.byteOffset + usable)),
    peak: r.peak,
  };
}

function cancelRecording() {
  const r = rec;
  rec = null;
  lastRms = 0;
  if (r) r.proc.kill();
}

// Falls back to the raw transcript whenever Ollama is off, slow or errors, so dictation never
// depends on it.
async function polish(text) {
  if (!cfg.polishModel || !text) return text;
  try {
    const r = await fetch(cfg.ollama + "/api/chat", {
      method: "POST",
      signal: AbortSignal.timeout(POLISH_TIMEOUT_MS),
      body: JSON.stringify({
        model: cfg.polishModel,
        stream: false,
        think: false,        // Qwen3.5 reasons first by default; tidying text doesn't need it
        keep_alive: "24h",   // a cold load costs seconds on the first dictation
        options: { temperature: 0 },
        messages: [{ role: "system", content: POLISH_PROMPT }, { role: "user", content: text }],
      }),
    });
    const j = await r.json();
    if (j.error) throw new Error(j.error);
    return j.message.content.trim() || text;
  } catch (err) {
    console.error("polish skipped:", err.message);
    return text;
  }
}

http.createServer(async (req, res) => {
  const send = (status, body) => {
    res.writeHead(status, { "Content-Type": "text/plain; charset=utf-8" });
    res.end(body);
  };
  const url = new URL(req.url, "http://127.0.0.1");
  switch (url.pathname) {
    case "/ping":
      return send(200, "OK");
    case "/state":
      return send(200, rec ? "REC" : "IDLE");
    case "/level": {
      // -60 dBFS .. 0 dBFS mapped to 0..100; raw RMS is far too small to plot directly
      const db = lastRms > 0 ? 20 * Math.log10(lastRms) : -100;
      return send(200, String(Math.max(0, Math.min(100, Math.round((db + 60) / 60 * 100)))));
    }
    case "/start": {
      const device = url.searchParams.get("device") || "";
      if (!GUID.test(device)) return send(400, "device must be a capture endpoint GUID");
      if (rec) return send(200, "REC");
      // Load the model while the user is still talking; the 3.6s load hides behind the recording.
      ensureAsr();
      touchAsr();
      try {
        await startRecording(device);
        return send(200, "REC");
      } catch (err) {
        if (!/Could not find audio only device/.test(err.message)) return send(500, err.message);
        // dshow's device cache can keep a headset's old endpoint GUID after it is re-paired or
        // moved to another USB port; dropping the cache makes dshow re-enumerate on the next open
        await new Promise((done) => spawn("reg", ["delete", DSHOW_AUDIO_CACHE, "/f"]).once("close", done).once("error", done));
        try {
          await startRecording(device);
          return send(200, "REC");
        } catch (retryErr) {
          return send(500, retryErr.message);
        }
      }
    }
    case "/stop": {
      const r = await stopRecording();
      if (!r) return send(200, "");
      if (r.peak < SILENT_PEAK) return send(422, "SILENT");
      try {
        const text = await transcribe(r.samples);
        touchAsr();
        return send(200, await polish(text));
      } catch (err) {
        console.error(err);
        return send(500, String(err));
      }
    }
    case "/cancel":
      cancelRecording();
      return send(200, "");
    default:
      return send(404, "");
  }
}).listen(cfg.port, "127.0.0.1", () => console.log("dictation ready on " + cfg.port));

killStrayAsr();
process.on("exit", () => { if (asr) asr.proc.kill(); });
