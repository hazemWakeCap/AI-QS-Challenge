// Shared loading + empty-state primitives so markup isn't re-duplicated per component.

export function Spinner({ label = "Loading…" }: { label?: string }) {
  return <div className="spinner">{label}</div>;
}

export function EmptyState({ icon = "○", title, hint }: { icon?: string; title: string; hint?: string }) {
  return (
    <div className="empty-state">
      <div className="empty-icon" aria-hidden>{icon}</div>
      <div className="empty-title">{title}</div>
      {hint && <div className="empty-hint">{hint}</div>}
    </div>
  );
}
