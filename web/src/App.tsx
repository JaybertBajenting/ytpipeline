import { Sidebar } from "./components/Sidebar";
import { Composer } from "./components/Composer";
import { Dashboard } from "./pages/Dashboard";
import { Videos } from "./pages/Videos";
import { Calendar } from "./pages/Calendar";
import { ChannelsPage } from "./pages/ChannelsPage";
import { SettingsPage } from "./pages/SettingsPage";
import { useUi } from "./lib/store";

export default function App() {
  const view = useUi((s) => s.view);

  return (
    <div className="flex h-full overflow-hidden">
      <Sidebar />

      {/* Each view owns its own scroll container so switching tabs doesn't inherit
          the previous view's scroll position. */}
      <main className="flex-1 overflow-y-auto">
        <div className="mx-auto max-w-[1180px] px-7 py-7">
          {view === "dashboard" && <Dashboard />}
          {view === "videos" && <Videos />}
          {view === "calendar" && <Calendar />}
          {view === "channels" && <ChannelsPage />}
          {view === "settings" && <SettingsPage />}
        </div>
      </main>

      {/* Global — reachable from the sidebar on any view. */}
      <Composer />
    </div>
  );
}
