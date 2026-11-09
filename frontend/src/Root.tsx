import { useSyncExternalStore } from "react";
import { App } from "./App";
import { AppView } from "./AppView";
import type { NotebookStore } from "./store";
import { WidgetContext } from "./Widgets";

export function Root({ store }: { store: NotebookStore }) {
  const mode = useSyncExternalStore(store.subscribe, () => store.getState().mode);
  return (
    <WidgetContext.Provider value={store.setWidget}>
      {mode === "app" ? <AppView store={store} /> : <App store={store} />}
    </WidgetContext.Provider>
  );
}
