// Simple browser + desktop notification helpers used by the Blazor UI.
(function () {
    "use strict";

    // Non-standard/app-specific globals are reached through a loosely typed alias so
    // the editor does not flag them as missing members of the global scope.
    /** @type {any} */
    const root = globalThis;

    const app = root.NexusApp || {};
    root.NexusApp = app;
    root.tradingApp = root.tradingApp || app;

    /**
     * Creates an AudioContext, falling back to the prefixed constructor on older browsers.
     * @returns {AudioContext | null}
     */
    function createAudioContext() {
        const Ctor = root.AudioContext || root.webkitAudioContext;
        return Ctor ? new Ctor() : null;
    }

    /**
     * Creates and shows a notification. The instance is returned rather than
     * constructed as a bare statement, which analyzers flag as a side-effecting `new`.
     * @param {string} title
     * @param {string} body
     * @returns {Notification}
     */
    function showNotification(title, body) {
        return new Notification(title, { body: body });
    }

    /**
     * Shows a desktop notification, requesting permission on first use.
     * @param {string} title
     * @param {string} body
     */
    app.notify = function (title, body) {
        try {
            if (!("Notification" in globalThis)) return;
            if (Notification.permission === "granted") {
                showNotification(title, body);
            } else if (Notification.permission !== "denied") {
                Notification.requestPermission().then(function (permission) {
                    if (permission === "granted") showNotification(title, body);
                });
            }
        } catch { /* ignore */ }
    };

    /** Plays a short generic tone, used when no specific cue is defined. */
    app.beep = function () {
        try {
            const ctx = createAudioContext();
            if (!ctx) return;

            const o = ctx.createOscillator();
            const g = ctx.createGain();
            o.type = "sine"; o.frequency.value = 880;
            g.gain.setValueAtTime(0.0001, ctx.currentTime);
            g.gain.exponentialRampToValueAtTime(0.2, ctx.currentTime + 0.01);
            g.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.35);
            o.connect(g); g.connect(ctx.destination);
            o.start(); o.stop(ctx.currentTime + 0.35);
        } catch { /* ignore */ }
    };

    // Distinct audio cues per trade event so the outcome is recognisable without
    // looking at the screen. Each entry is a sequence of tones:
    //   [frequency Hz, duration s, start offset s, waveform, gain]
    app.soundPatterns = {
        // Entries - rising. Live is a louder, harder square wave; paper is a soft sine.
        "live-entry":  [[660, 0.12, 0, "square", 0.22], [990, 0.18, 0.13, "square", 0.22]],
        "paper-entry": [[880, 0.18, 0, "sine", 0.14]],

        // Exits - falling, mirroring the entry timbre.
        "live-exit":   [[700, 0.12, 0, "square", 0.22], [440, 0.22, 0.13, "square", 0.22]],
        "paper-exit":  [[520, 0.22, 0, "sine", 0.14]],

        // Manual square-off - three short, deliberate blips.
        "squareoff":   [[600, 0.08, 0, "triangle", 0.2], [600, 0.08, 0.12, "triangle", 0.2], [600, 0.08, 0.24, "triangle", 0.2]],

        // Stop-loss - low, harsh, unmistakable.
        "sl-hit":      [[240, 0.3, 0, "sawtooth", 0.25], [180, 0.4, 0.28, "sawtooth", 0.25]],

        // Target - bright ascending triad.
        "target-hit":  [[784, 0.1, 0, "sine", 0.2], [988, 0.1, 0.1, "sine", 0.2], [1319, 0.22, 0.2, "sine", 0.2]],

        // New signal that needs a manual decision (Parsed/Pending) - urgent, attention-
        // grabbing double-beep so it stands out from informational cues.
        "signal-manual": [[1046, 0.14, 0, "square", 0.24], [1046, 0.14, 0.2, "square", 0.24]],

        // New signal armed for auto-execution (AwaitingActivation/AwaitingEntry) - a
        // single soft chime; informational only, no action required yet.
        "signal-auto":   [[523, 0.16, 0, "sine", 0.14]]
    };

    /**
     * Plays the named cue from soundPatterns, falling back to a plain beep.
     * @param {string} name
     */
    app.playSound = function (name) {
        try {
            const pattern = app.soundPatterns[name];
            if (!pattern) { app.beep(); return; }

            const ctx = createAudioContext();
            if (!ctx) return;

            pattern.forEach(function (tone) {
                const freq = tone[0], duration = tone[1], offset = tone[2];
                const wave = tone[3] || "sine", peak = tone[4] || 0.2;

                const o = ctx.createOscillator();
                const g = ctx.createGain();
                const start = ctx.currentTime + offset;

                o.type = wave;
                o.frequency.setValueAtTime(freq, start);
                g.gain.setValueAtTime(0.0001, start);
                g.gain.exponentialRampToValueAtTime(peak, start + 0.01);
                g.gain.exponentialRampToValueAtTime(0.0001, start + duration);

                o.connect(g); g.connect(ctx.destination);
                o.start(start); o.stop(start + duration + 0.02);
            });
        } catch { /* ignore */ }
    };

    /**
     * Triggers a browser download for content produced on the server. The payload is
     * passed as base64 so any encoding (UTF-8 BOM, commas, newlines) survives interop.
     * @param {string} fileName
     * @param {string} contentType
     * @param {string} base64
     */
    app.downloadFile = function (fileName, contentType, base64) {
        try {
            const binary = atob(base64);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) {
                bytes[i] = binary.codePointAt(i) ?? 0;
            }

            const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
            const url = URL.createObjectURL(blob);
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName || "download";
            document.body.append(link);
            link.click();
            link.remove();
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
        } catch { /* ignore */ }
    };

    // Backward-compatible aliases
    root.tradingApp.notify = app.notify;
    root.tradingApp.beep = app.beep;
    root.tradingApp.playSound = app.playSound;
    root.tradingApp.downloadFile = app.downloadFile;
})();
