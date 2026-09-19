import { useEffect, useState, type FormEvent } from "react";
import { Link, NavLink, Route, Routes, useLocation } from "react-router-dom";
import { describeError, getCurrentAssembly, getHealth, type Health } from "./api";
import { clearToken, setToken, tokenWasRejected, useToken } from "./auth";
import { DirectorPage } from "./director/DirectorPage";
import { HistoryPage } from "./history/HistoryPage";
import { PreviewPage } from "./preview/PreviewPage";
import { SimPage } from "./sim/SimPage";
import { KitchenPage } from "./kitchen/KitchenPage";
import { ReviewPage } from "./review/ReviewPage";
import { UploadPage } from "./upload/UploadPage";
import { ThemeSwitch } from "./ThemeSwitch";

/* Glyphs: 24px grid, 1.6 stroke, like mayim's. */
const ICON = { viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 1.6, "aria-hidden": true } as const;
const MARK = (
  <svg {...ICON} strokeWidth={1.8}>
    <rect x="4" y="4" width="16" height="16" />
    <path d="M4 20 20 4" strokeLinecap="square" />
  </svg>
);
const KEY = (
  <svg {...ICON}>
    <circle cx="8" cy="15" r="4" />
    <path d="m11 12 8-8M16 7l2.5 2.5" strokeLinecap="round" />
  </svg>
);

const PAGES: { to: string; label: string }[] = [
  { to: "/director", label: "Director" },
  { to: "/upload", label: "Upload" },
  { to: "/history", label: "History" },
  { to: "/preview", label: "Preview" },
  { to: "/sim", label: "Sim" },
];

/** Polls /health for the top bar's status pill. */
function useHealth(): { health: Health | null; error: string | null } {
  const [health, setHealth] = useState<Health | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    const load = () =>
      getHealth().then(
        (h) => { if (alive) { setHealth(h); setError(null); } },
        (e) => { if (alive) { setHealth(null); setError(describeError(e)); } },
      );
    void load();
    const timer = window.setInterval(load, 5000);
    return () => { alive = false; window.clearInterval(timer); };
  }, []);
  return { health, error };
}

/** The only chrome: the name, the pages, and at the right the server's state and the two settings. */
function TopBar({ token, health, error }: { token: string | null; health: Health | null; error: string | null }) {
  const state = error ? "bad" : health?.ok ? "ok" : "idle";
  return (
    <header className="topbar">
      <Link to="/" className="tb-brand">{MARK}<span>Cut Once</span></Link>
      <nav className="tb-nav" aria-label="Pages">
        {PAGES.map((p) => (
          <NavLink key={p.to} to={p.to} className={({ isActive }) => (isActive ? "active" : "")}>{p.label}</NavLink>
        ))}
      </nav>
      <div className="tb-spacer" />
      <span className={`ch-pill ${state}`} title={error ?? undefined}>
        {error ? "Server unreachable" : health?.ok ? "Server live" : "Checking server…"}
      </span>
      <ThemeSwitch />
      {token && (
        <button type="button" className="tb-ico" title="Forget the API token" aria-label="Forget the API token" onClick={() => clearToken()}>
          {KEY}
        </button>
      )}
    </header>
  );
}

function TokenPrompt() {
  const [value, setValue] = useState("");
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (value.trim()) setToken(value);
  };
  return (
    <form className="card token-prompt" onSubmit={submit}>
      <h2>API token</h2>
      <p className="muted">Paste the server's <span className="mono">API_TOKEN</span>. This browser keeps it.</p>
      {tokenWasRejected() && <p className="error-text">The server rejected the last token. Enter it again.</p>}
      <div className="row">
        <input
          type="password" autoFocus autoComplete="off" spellCheck={false} placeholder="API_TOKEN"
          value={value} onChange={(e) => setValue(e.target.value)} aria-label="API token"
        />
        <button type="submit" className="primary" disabled={!value.trim()}>Save</button>
      </div>
    </form>
  );
}

function HomePage() {
  const [planId, setPlanId] = useState("");
  useEffect(() => {
    let alive = true;
    getCurrentAssembly().then(
      (a) => { if (alive) setPlanId((cur) => cur || a.plan_id); },
      () => { /* No run yet, or the server is down: the health panel says which. */ },
    );
    return () => { alive = false; };
  }, []);

  return (
    <main className="page home">
      <div className="home-links">
        <Link className="link-card" to="/director"><h2>Director</h2><p>Run the demo.</p></Link>
        <Link className="link-card" to="/upload"><h2>Upload</h2><p>Turn a drawing into a plan.</p></Link>
        <div className="link-card">
          <h2>Review</h2>
          <p>Check a plan against its drawing and approve it.</p>
          <div className="row">
            <input value={planId} onChange={(e) => setPlanId(e.target.value.trim())} placeholder="plan_id" aria-label="Plan id" />
            {planId
              ? <Link className="button" to={`/review/${encodeURIComponent(planId)}`}>Open</Link>
              : <span className="button disabled">Open</span>}
          </div>
        </div>
        <Link className="link-card" to="/history"><h2>History</h2><p>Replay every version of the build.</p></Link>
      </div>
    </main>
  );
}

function NotFound() {
  return (
    <main className="page">
      <div className="page-title"><h1>Page not found</h1></div>
      <p><Link to="/">Back to the start</Link></p>
    </main>
  );
}

/** Pages that fill the whole window, like the headset's view: no top bar. */
const BARE_PAGES = new Set(["/preview", "/sim", "/kitchen"]);

export function App() {
  const token = useToken();
  const { pathname } = useLocation();
  const { health, error } = useHealth();
  if (token && BARE_PAGES.has(pathname)) {
    return (
      <Routes>
        <Route path="/preview" element={<PreviewPage />} />
        <Route path="/sim" element={<SimPage />} />
        <Route path="/kitchen" element={<KitchenPage />} />
      </Routes>
    );
  }
  return (
    <>
      <div className="shell">
        <TopBar token={token} health={health} error={error} />
        <div className="view">
          {token ? (
            <Routes>
              <Route path="/" element={<HomePage />} />
              <Route path="/director" element={<DirectorPage />} />
              <Route path="/upload" element={<UploadPage />} />
              <Route path="/review/:planId" element={<ReviewPage />} />
              <Route path="/history" element={<HistoryPage />} />
              <Route path="*" element={<NotFound />} />
            </Routes>
          ) : (
            <main className="page">
              <TokenPrompt />
            </main>
          )}
        </div>
      </div>
    </>
  );
}
