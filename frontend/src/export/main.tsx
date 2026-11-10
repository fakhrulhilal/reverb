import { createRoot } from "react-dom/client";
import "../theme";
import "../styles.css";
import "./export.css";
import { ExportView, type ExportedNotebook } from "./ExportView";

// The notebook is in the page as JSON, written by `reverb export`.
const data = JSON.parse(document.getElementById("reverb-notebook")!.textContent!) as ExportedNotebook;
createRoot(document.getElementById("root")!).render(<ExportView notebook={data} />);
