// Smart-X console - the only JavaScript in the client.
//
// Everything else is C# running on WebAssembly. This exists because page
// visibility is a browser event with no .NET equivalent: the Anomaly Radar uses it
// to stop polling while its tab is hidden, since a background tab requesting
// fresh telemetry every three seconds for an hour is a lot of pointless load on
// the gateway.

window.smartx = {
    /**
     * Calls OnVisibilityChanged(bool) on the .NET object whenever the tab is shown
     * or hidden. Returns a handle to pass to stopVisibility.
     */
    watchVisibility: function (dotnetRef) {
        const handler = () => dotnetRef.invokeMethodAsync('OnVisibilityChanged', !document.hidden);
        document.addEventListener('visibilitychange', handler);
        return { dispose: () => document.removeEventListener('visibilitychange', handler) };
    },

    stopVisibility: function (handle) {
        if (handle && handle.dispose) {
            handle.dispose();
        }
    }
};
