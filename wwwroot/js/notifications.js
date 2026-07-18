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

// Backward-compatible aliases
window.tradingApp.notify = window.NexusApp.notify;
window.tradingApp.beep = window.NexusApp.beep;
