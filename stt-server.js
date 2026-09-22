// Local dictation server. Reuses Orca's bundled sherpa-onnx addon + downloaded model.
// GET /start?device=<capture endpoint GUID> -> "REC" once audio is flowing, 500 with the ffmpeg error otherwise
// GET /stop   -> transcript; 422 "SILENT" when the mic delivered nothing but silence
// GET /cancel -> stop and discard
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");

const cfgPath = path.join(__dirname, "config.json");
const cfg = {
  // Defaults point at the copies the Orca client ships; config.json can move them anywhere.
  sherpaModule: path.join(process.env.LOCALAPPDATA || "", "Programs/orca/resources/node_modules/sherpa-onnx-win-x64"),
  modelDir: path.join(process.env.APPDATA || "", "orca/speech-models/sense-voice-zh-en-ja-ko-yue"),
  ffmpeg: "ffmpeg",
  port: 8377,
  ...(fs.existsSync(cfgPath) ? JSON.parse(fs.readFileSync(cfgPath, "utf8")) : {}),
};

const SAMPLE_RATE = 16000;
const CHUNK_SAMPLES = SAMPLE_RATE * 30; // sherpa offline decode window
const SILENT_PEAK = 0.001;             // -60 dBFS; a muted or dead mic never gets near this
const START_TIMEOUT_MS = 5000;
const DSHOW_AUDIO_CACHE = "HKCU\\Software\\Microsoft\\ActiveMovie\\devenum 64-bit\\{33D9A762-90C8-11D0-BD43-00A0C911CE86}";
const GUID = /^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$/i;

const sherpa = require(cfg.sherpaModule);
const recognizer = sherpa.createOfflineRecognizer({
  featConfig: { sampleRate: SAMPLE_RATE, featureDim: 80 },
  modelConfig: {
    senseVoice: {
      model: path.join(cfg.modelDir, "model.int8.onnx"),
      language: "",
      useInverseTextNormalization: 1,
    },
    tokens: path.join(cfg.modelDir, "tokens.txt"),
    numThreads: 2,
    provider: "cpu",
    debug: 0,
  },
  decodingMethod: "greedy_search",
});

let rec = null;   // recording in progress
let lastRms = 0;  // 0..1, most recent audio block

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

function decode(samples) {
  const parts = [];
  for (let i = 0; i < samples.length; i += CHUNK_SAMPLES) {
    const stream = sherpa.createOfflineStream(recognizer);
    sherpa.acceptWaveformOffline(stream, {
      sampleRate: SAMPLE_RATE,
      samples: samples.slice(i, i + CHUNK_SAMPLES),
    });
    sherpa.decodeOfflineStream(recognizer, stream);
    const text = (JSON.parse(sherpa.getOfflineStreamResultAsJson(stream)).text || "").trim();
    if (text) parts.push(text);
  }
  return parts.join(" ");
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
        // decode is synchronous and blocks the server; dictation is one-at-a-time anyway
        return send(200, decode(r.samples));
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
