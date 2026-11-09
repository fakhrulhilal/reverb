import { createRoot } from "react-dom/client";
import { Root } from "./Root";
import { NotebookStore } from "./store";
import "./styles.css";

// The token arrives in the URL printed by `reverb edit`. Keep it for reloads of this tab, but take
// it out of the address bar so it doesn't end up in history or screenshots.
function takeToken(): string {
  const url = new URL(window.location.href);
  const fromUrl = url.searchParams.get("token");
  let token = fromUrl ?? "";
  try {
    if (fromUrl) sessionStorage.setItem("reverb-token", fromUrl);
    else token = sessionStorage.getItem("reverb-token") ?? "";
  } catch {
    // Storage can be blocked; the token in the URL still works for this load.
  }
  if (fromUrl) {
    url.searchParams.delete("token");
    window.history.replaceState(null, "", url.pathname + url.search + url.hash);
  }
  return token;
}

const token = takeToken();
const scheme = window.location.protocol === "https:" ? "wss" : "ws";
const store = new NotebookStore(`${scheme}://${window.location.host}/ws?token=${encodeURIComponent(token)}`);
store.connect();

createRoot(document.getElementById("root")!).render(<Root store={store} />);
