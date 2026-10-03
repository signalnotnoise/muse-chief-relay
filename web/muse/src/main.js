import { createApp, h, ref } from "vue";
import App from "./App.vue";
import WatchLive from "./WatchLive.vue";
import { isWatchRoute } from "./watchRoute.js";
import "./styles.css";

// Tiny hash router. Exactly "#/watch" (or "#/watch/") mounts the read-only
// live spectator view; anything else, including "#/watchdog", mounts the
// interactive client. Inside the client, App.vue switches between its chat
// and board sections on "#/board" (see navRoute.js). The hash is only ever
// read, never written, and switching views unmounts the previous root cleanly.
function pick() {
  return isWatchRoute(window.location.hash) ? WatchLive : App;
}

const current = ref(pick());
window.addEventListener("hashchange", () => {
  current.value = pick();
});

// Mobile keyboards (notably iOS Safari) overlay the layout viewport instead of
// resizing it, which would bury the pinned composer under the keyboard. Pin
// #app to the *visual* viewport height so the transcript shrinks and the send
// box stays reachable while typing. The viewport meta's
// interactive-widget=resizes-content covers Chrome on Android; this covers the
// rest. Guarded: no visualViewport (old browsers) means no behavior change.
function syncViewportHeight() {
  const vv = window.visualViewport;
  if (!vv) return;
  const app = document.getElementById("app");
  if (app) app.style.height = Math.round(vv.height) + "px";
}
if (window.visualViewport) {
  window.visualViewport.addEventListener("resize", syncViewportHeight);
  syncViewportHeight();
}

createApp({ setup: () => () => h(current.value) }).mount("#app");
