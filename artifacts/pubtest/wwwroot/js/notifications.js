// Simple browser + desktop notification helpers used by the Blazor UI.
window.NexusApp = window.NexusApp || {};
window.tradingApp = window.tradingApp || window.NexusApp;

window.NexusApp.notify = function (title, body) {
    try {
        if (!("Notification" in window)) return;
        if (Notification.permission === "granted") {
            new Notification(title, { body: body });
        } else if (Notification.permission !== "denied") {
            Notification.requestPermission().then(p => {
                if (p === "granted") new Notification(title, { body: body });
            });
        }
    } catch (_) { /* ignore */ }
};

window.NexusApp.beep = function () {
    try {
        var ctx = new (window.AudioContext || window.webkitAudioContext)();
        var o = ctx.createOscillator();
        var g = ctx.createGain();
        o.type = "sine"; o.frequency.value = 880;
        g.gain.setValueAtTime(0.0001, ctx.currentTime);
        g.gain.exponentialRampToValueAtTime(0.2, ctx.currentTime + 0.01);
        g.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.35);
        o.connect(g); g.connect(ctx.destination);
        o.start(); o.stop(ctx.currentTime + 0.35);
    } catch (_) { /* ignore */ }
};

// Distinct audio cues per trade event so the outcome is recognisable without
// looking at the screen. Each entry is a sequence of tones:
//   [frequency Hz, duration s, start offset s, waveform, gain]
window.NexusApp.soundPatterns = {
    // Entries - rising. Live is a louder, harder square wave; paper is a soft sine.
    "live-entry":  [[660, 0.12, 0.00, "square", 0.22], [990, 0.18, 0.13, "square", 0.22]],
    "paper-entry": [[880, 0.18, 0.00, "sine", 0.14]],

    // Exits - falling, mirroring the entry timbre.
    "live-exit":   [[700, 0.12, 0.00, "square", 0.22], [440, 0.22, 0.13, "square", 0.22]],
    "paper-exit":  [[520, 0.22, 0.00, "sine", 0.14]],

    // Manual square-off - three short, deliberate blips.
    "squareoff":   [[600, 0.08, 0.00, "triangle", 0.20], [600, 0.08, 0.12, "triangle", 0.20], [600, 0.08, 0.24, "triangle", 0.20]],

    // Stop-loss - low, harsh, unmistakable.
    "sl-hit":      [[240, 0.30, 0.00, "sawtooth", 0.25], [180, 0.40, 0.28, "sawtooth", 0.25]],

    // Target - bright ascending triad.
    "target-hit":  [[784, 0.10, 0.00, "sine", 0.20], [988, 0.10, 0.10, "sine", 0.20], [1319, 0.22, 0.20, "sine", 0.20]]
};

window.NexusApp.playSound = function (name) {
    try {
        var pattern = window.NexusApp.soundPatterns[name];
        if (!pattern) { window.NexusApp.beep(); return; }

        var ctx = new (window.AudioContext || window.webkitAudioContext)();
        pattern.forEach(function (tone) {
            var freq = tone[0], duration = tone[1], offset = tone[2];
            var wave = tone[3] || "sine", peak = tone[4] || 0.2;

            var o = ctx.createOscillator();
            var g = ctx.createGain();
            var start = ctx.currentTime + offset;

            o.type = wave;
            o.frequency.setValueAtTime(freq, start);
            g.gain.setValueAtTime(0.0001, start);
            g.gain.exponentialRampToValueAtTime(peak, start + 0.01);
            g.gain.exponentialRampToValueAtTime(0.0001, start + duration);

            o.connect(g); g.connect(ctx.destination);
            o.start(start); o.stop(start + duration + 0.02);
        });
    } catch (_) { /* ignore */ }
};

// Backward-compatible aliases
window.tradingApp.notify = window.NexusApp.notify;
window.tradingApp.beep = window.NexusApp.beep;
window.tradingApp.playSound = window.NexusApp.playSound;
