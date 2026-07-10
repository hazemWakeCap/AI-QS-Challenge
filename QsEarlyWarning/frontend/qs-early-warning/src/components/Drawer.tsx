import { useEffect, useRef, type ReactNode } from "react";
import { createPortal } from "react-dom";

// A right-side slide-in inspector drawer. Portal-rendered to document.body so it escapes the tab's
// stacking/overflow context. NON-modal by design: the scrim is a purely visual dim (pointer-events:
// none), so the list behind it stays interactive — clicking another row swaps the drawer's contents
// in place. Closes on Esc or the ✕ button. Restores focus to the trigger on close.
export function Drawer({
  open,
  onClose,
  title,
  children,
}: {
  open: boolean;
  onClose: () => void;
  title?: ReactNode;
  children: ReactNode;
}) {
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    const prevFocus = document.activeElement as HTMLElement | null;
    closeRef.current?.focus();

    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);

    return () => {
      window.removeEventListener("keydown", onKey);
      prevFocus?.focus?.();
    };
  }, [open, onClose]);

  if (!open) return null;

  return createPortal(
    <>
      <div className="drawer-scrim" aria-hidden />
      <aside className="drawer" role="dialog" aria-label="Variance attribution">
        <div className="drawer-head">
          <div className="drawer-title">{title}</div>
          <button ref={closeRef} className="drawer-close" onClick={onClose} aria-label="Close">×</button>
        </div>
        <div className="drawer-body">{children}</div>
      </aside>
    </>,
    document.body,
  );
}
