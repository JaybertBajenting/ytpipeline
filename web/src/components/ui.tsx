import { cva, type VariantProps } from "class-variance-authority";
import { AlertTriangle, Check, ChevronDown, Copy, Info, Loader2, X } from "lucide-react";
import {
  createContext, useContext, useEffect, useId, useState,
  type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode,
  type SelectHTMLAttributes, type TextareaHTMLAttributes,
} from "react";
import { cn } from "@/lib/utils";

// ─── Button ──────────────────────────────────────────────────────────────────

const button = cva(
  "relative inline-flex items-center justify-center gap-1.5 rounded-lg font-medium transition-all duration-150 disabled:opacity-40 disabled:cursor-not-allowed focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-amber-500/50 focus-visible:ring-offset-2 focus-visible:ring-offset-[#0a0a0b] select-none whitespace-nowrap",
  {
    variants: {
      variant: {
        primary:
          "bg-amber-500 text-black shadow-sm shadow-amber-500/20 hover:bg-amber-400 active:bg-amber-600",
        secondary:
          "border border-white/[0.1] bg-white/[0.04] text-white/80 hover:bg-white/[0.08] hover:border-white/[0.18] hover:text-white active:bg-white/[0.06]",
        ghost:
          "text-white/50 hover:bg-white/[0.06] hover:text-white/85 active:bg-white/[0.04]",
        danger:
          "border border-red-500/30 bg-red-950/50 text-red-400 hover:bg-red-900/50 hover:border-red-500/50 active:bg-red-950/70",
        subtle:
          "bg-white/[0.03] text-white/60 hover:bg-white/[0.07] hover:text-white/90",
      },
      size: {
        xs:    "h-6 px-2 text-xs gap-1",
        sm:    "h-7 px-2.5 text-xs",
        md:    "h-8 px-3.5 text-sm",
        lg:    "h-10 px-5 text-sm",
        icon:  "size-8 p-0",
        block: "h-9 w-full px-4 text-sm",
      },
    },
    defaultVariants: { variant: "secondary", size: "md" },
  }
);

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof button> {
  loading?: boolean;
}

export function Button({ className, variant, size, loading, children, disabled, ...props }: ButtonProps) {
  return (
    <button className={cn(button({ variant, size }), className)} disabled={disabled || loading} {...props}>
      {loading && <Loader2 className="size-3.5 animate-spin" />}
      {children}
    </button>
  );
}

// ─── Card ────────────────────────────────────────────────────────────────────

const card = cva("rounded-xl border transition-all duration-200", {
  variants: {
    tone: {
      base:  "border-white/[0.07] bg-white/[0.02]",
      raised:"border-white/[0.08] bg-[#16161a]",
      ok:    "border-emerald-500/20 bg-emerald-500/[0.03]",
      run:   "border-amber-500/40 bg-amber-500/[0.04] shadow-lg shadow-amber-500/5",
      err:   "border-red-500/25 bg-red-500/[0.04]",
    },
    interactive: {
      true: "cursor-pointer hover:border-white/[0.16] hover:bg-white/[0.04] active:scale-[0.995]",
      false: "",
    },
  },
  defaultVariants: { tone: "base", interactive: false },
});

export function Card({
  className, children, tone, interactive, ...rest
}: { className?: string; children: ReactNode } & VariantProps<typeof card>
  & React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={cn(card({ tone, interactive }), className)} {...rest}>
      {children}
    </div>
  );
}

/** Card header with a title, optional description, and a right-hand slot. */
export function CardHead({
  title, desc, right, icon,
}: { title: ReactNode; desc?: ReactNode; right?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="flex items-start gap-3 border-b border-white/[0.06] px-4 py-3">
      {icon && (
        <span className="mt-0.5 grid size-7 shrink-0 place-items-center rounded-lg border border-white/[0.08] bg-white/[0.03] text-white/50">
          {icon}
        </span>
      )}
      <div className="min-w-0 flex-1">
        <h3 className="text-sm font-semibold text-white/90">{title}</h3>
        {desc && <p className="mt-0.5 text-xs leading-relaxed text-white/40">{desc}</p>}
      </div>
      {right}
    </div>
  );
}

// ─── Badge ───────────────────────────────────────────────────────────────────

const badge = cva(
  "inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium border whitespace-nowrap",
  {
    variants: {
      tone: {
        muted:   "border-white/[0.1]  bg-white/[0.04] text-white/40",
        ok:      "border-emerald-500/30 bg-emerald-500/10 text-emerald-400",
        warn:    "border-amber-500/30  bg-amber-500/10  text-amber-400",
        err:     "border-red-500/30    bg-red-500/10    text-red-400",
        run:     "border-amber-500/40  bg-amber-500/10  text-amber-400",
        channel: "border-white/[0.12]  bg-white/[0.05]  text-white/60",
        info:    "border-sky-500/30    bg-sky-500/10    text-sky-300",
      },
    },
    defaultVariants: { tone: "muted" },
  }
);

export function Badge({ tone, children, className }: VariantProps<typeof badge> & { children: ReactNode; className?: string }) {
  return <span className={cn(badge({ tone }), className)}>{children}</span>;
}

// ─── Tag ─────────────────────────────────────────────────────────────────────

export function Tag({ children }: { children: ReactNode }) {
  return (
    <span className="m-0.5 inline-block rounded-full border border-white/[0.08] bg-white/[0.04] px-2 py-0.5 text-[11px] text-white/40">
      {children}
    </span>
  );
}

// ─── Spinner ─────────────────────────────────────────────────────────────────

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={cn("size-3.5 animate-spin", className)} />;
}

// ─── PipelineNode ────────────────────────────────────────────────────────────

export function PipelineNode({
  num, done, running,
}: { num: ReactNode; done?: boolean; running?: boolean }) {
  return (
    <span
      className={cn(
        "grid size-6 shrink-0 place-items-center rounded-full text-xs font-bold transition-all duration-300",
        done    && "bg-emerald-500 text-black",
        running && "bg-amber-500 text-black pulse-amber",
        !done && !running && "border border-white/[0.12] bg-white/[0.04] text-white/40",
      )}
    >
      {done ? <Check className="size-3.5" strokeWidth={3} /> : num}
    </span>
  );
}

// ─── SectionLabel ────────────────────────────────────────────────────────────

export function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <div className="text-[10px] font-semibold uppercase tracking-widest text-white/30">
      {children}
    </div>
  );
}

/** Page title block used at the top of every view. */
export function PageHead({
  title, desc, actions,
}: { title: ReactNode; desc?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-6 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 className="text-[22px] font-semibold leading-tight tracking-tight text-white">{title}</h1>
        {desc && <p className="mt-1 text-sm text-white/40">{desc}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}

// ─── Form primitives ─────────────────────────────────────────────────────────

export const inputCls =
  "w-full rounded-lg border border-white/[0.08] bg-white/[0.04] px-3 py-2 text-sm text-white/90 placeholder-white/25 outline-none transition-colors focus:border-amber-500/50 focus:bg-white/[0.06]";

export const selectCls =
  "rounded-lg border border-white/[0.08] bg-[#161618] px-3 py-2 text-sm text-white/90 outline-none transition-colors focus:border-amber-500/50 appearance-none cursor-pointer";

/** Label + optional hint wrapper. Generates the id so the label actually points at the input. */
export function Field({
  label, hint, children, className, htmlFor,
}: { label?: ReactNode; hint?: ReactNode; children: ReactNode; className?: string; htmlFor?: string }) {
  return (
    <div className={cn("min-w-0", className)}>
      {label && (
        <label htmlFor={htmlFor} className="mb-1.5 block text-xs font-medium text-white/55">
          {label}
        </label>
      )}
      {children}
      {hint && <p className="mt-1.5 text-[11px] leading-relaxed text-white/30">{hint}</p>}
    </div>
  );
}

export function Input({
  label, hint, className, ...props
}: { label?: ReactNode; hint?: ReactNode } & InputHTMLAttributes<HTMLInputElement>) {
  const id = useId();
  return (
    <Field label={label} hint={hint} htmlFor={id}>
      <input id={id} className={cn(inputCls, className)} {...props} />
    </Field>
  );
}

export function Textarea({
  label, hint, className, rows = 3, ...props
}: { label?: ReactNode; hint?: ReactNode } & TextareaHTMLAttributes<HTMLTextAreaElement>) {
  const id = useId();
  return (
    <Field label={label} hint={hint} htmlFor={id}>
      <textarea id={id} rows={rows} className={cn(inputCls, "resize-y leading-relaxed", className)} {...props} />
    </Field>
  );
}

export function Select({
  label, hint, className, children, ...props
}: { label?: ReactNode; hint?: ReactNode } & SelectHTMLAttributes<HTMLSelectElement>) {
  const id = useId();
  return (
    <Field label={label} hint={hint} htmlFor={id}>
      <div className="relative">
        <select id={id} className={cn(selectCls, "w-full pr-9", className)} {...props}>
          {children}
        </select>
        <ChevronDown className="pointer-events-none absolute right-3 top-1/2 size-3.5 -translate-y-1/2 text-white/30" />
      </div>
    </Field>
  );
}

/**
 * Password-style input for a credential. When the server reports the value is already
 * stored, the placeholder says so and an empty submit leaves it untouched — that's what
 * makes "save" safe to press without re-pasting every token.
 */
export function SecretInput({
  label, hint, saved, value, onChange, placeholder,
}: {
  label: ReactNode; hint?: ReactNode; saved?: boolean;
  /** null = untouched (server keeps its value); "" = clear it; string = replace. */
  value: string | null; onChange: (v: string | null) => void; placeholder?: string;
}) {
  const [reveal, setReveal] = useState(false);
  const id = useId();
  const clearing = value === "";
  const typing = !!value;

  return (
    <Field
      label={
        <span className="flex items-center gap-2">
          {label}
          {clearing
            ? <Badge tone="err">will be cleared</Badge>
            : typing
              ? <Badge tone="warn">will be replaced</Badge>
              : saved
                ? <Badge tone="ok"><Check className="size-2.5" strokeWidth={3} /> saved</Badge>
                : <Badge tone="muted">not set</Badge>}
        </span>
      }
      hint={hint}
      htmlFor={id}
    >
      <div className="relative">
        <input
          id={id}
          type={reveal ? "text" : "password"}
          className={cn(inputCls, "pr-24 font-mono text-xs", clearing && "border-red-500/40")}
          value={value ?? ""}
          autoComplete="off"
          spellCheck={false}
          placeholder={placeholder ?? (saved ? "•••••••• unchanged" : "paste value")}
          // Emptying the box returns to "untouched" rather than "clear" — clearing is
          // deliberate, via the button, so a stray backspace can't wipe a live token.
          onChange={(e) => onChange(e.target.value === "" ? null : e.target.value)}
        />
        <div className="absolute right-1.5 top-1/2 flex -translate-y-1/2 items-center gap-0.5">
          {saved && !typing && (
            <button
              type="button"
              onClick={() => onChange(clearing ? null : "")}
              title={clearing ? "Keep the stored value" : "Clear the stored value on save"}
              className={cn(
                "rounded px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide transition-colors",
                clearing
                  ? "bg-red-500/15 text-red-400 hover:bg-red-500/25"
                  : "text-white/35 hover:bg-white/[0.08] hover:text-red-400",
              )}
            >
              {clearing ? "undo" : "clear"}
            </button>
          )}
          <button
            type="button"
            onClick={() => setReveal((v) => !v)}
            className="rounded px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-white/35 transition-colors hover:bg-white/[0.08] hover:text-white/70"
          >
            {reveal ? "hide" : "show"}
          </button>
        </div>
      </div>
    </Field>
  );
}

// ─── Switch ──────────────────────────────────────────────────────────────────

export function Switch({
  checked, onChange, label, desc, className,
}: { checked: boolean; onChange: (v: boolean) => void; label?: ReactNode; desc?: ReactNode; className?: string }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className={cn(
        "flex w-full items-center gap-3 rounded-lg border px-3 py-2.5 text-left transition-colors",
        checked
          ? "border-amber-500/30 bg-amber-500/[0.08]"
          : "border-white/[0.07] bg-white/[0.02] hover:border-white/[0.12]",
        className,
      )}
    >
      <span className={cn(
        "relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors duration-200",
        checked ? "bg-amber-500" : "bg-white/[0.12]",
      )}>
        <span className={cn(
          "absolute size-3.5 rounded-full shadow transition-transform duration-200",
          checked ? "translate-x-[18px] bg-black" : "translate-x-[3px] bg-white",
        )} />
      </span>
      {(label || desc) && (
        <span className="min-w-0 flex-1">
          {label && (
            <span className={cn("block text-xs font-medium", checked ? "text-amber-400" : "text-white/70")}>
              {label}
            </span>
          )}
          {desc && <span className="mt-0.5 block text-[11px] leading-relaxed text-white/30">{desc}</span>}
        </span>
      )}
    </button>
  );
}

// ─── Slider ──────────────────────────────────────────────────────────────────

export function Slider({
  label, value, onChange, min, max, step, format, hint,
}: {
  label: ReactNode; value: number; onChange: (v: number) => void;
  min: number; max: number; step: number; format?: (v: number) => string; hint?: ReactNode;
}) {
  const pct = ((value - min) / (max - min)) * 100;
  return (
    <div>
      <div className="mb-2 flex items-center justify-between">
        <label className="text-xs font-medium text-white/55">{label}</label>
        <span className="rounded bg-white/[0.06] px-1.5 py-0.5 font-mono text-[11px] tabular-nums text-amber-400">
          {format ? format(value) : value}
        </span>
      </div>
      <input
        type="range"
        min={min} max={max} step={step} value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        className="h-1.5 w-full cursor-pointer appearance-none rounded-full outline-none
                   [&::-webkit-slider-thumb]:size-3.5 [&::-webkit-slider-thumb]:appearance-none
                   [&::-webkit-slider-thumb]:rounded-full [&::-webkit-slider-thumb]:bg-amber-500
                   [&::-webkit-slider-thumb]:shadow [&::-webkit-slider-thumb]:transition-transform
                   [&::-webkit-slider-thumb]:hover:scale-110"
        style={{ background: `linear-gradient(90deg, var(--amber) ${pct}%, rgba(255,255,255,0.1) ${pct}%)` }}
      />
      {hint && <p className="mt-1.5 text-[11px] leading-relaxed text-white/30">{hint}</p>}
    </div>
  );
}

// ─── Tabs ────────────────────────────────────────────────────────────────────

const TabsCtx = createContext<{ value: string; set: (v: string) => void } | null>(null);

export function Tabs({
  value, onChange, children,
}: { value: string; onChange: (v: string) => void; children: ReactNode }) {
  return <TabsCtx.Provider value={{ value, set: onChange }}>{children}</TabsCtx.Provider>;
}

export function TabList({ children }: { children: ReactNode }) {
  return (
    <div className="mb-5 flex gap-1 overflow-x-auto border-b border-white/[0.07] hide-scrollbar">
      {children}
    </div>
  );
}

export function TabTrigger({ id, icon, children }: { id: string; icon?: ReactNode; children: ReactNode }) {
  const ctx = useContext(TabsCtx)!;
  const active = ctx.value === id;
  return (
    <button
      onClick={() => ctx.set(id)}
      className={cn(
        "-mb-px flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium transition-colors",
        active
          ? "border-amber-500 text-amber-400"
          : "border-transparent text-white/40 hover:border-white/[0.12] hover:text-white/70",
      )}
    >
      {icon}
      {children}
    </button>
  );
}

export function TabPanel({ id, children }: { id: string; children: ReactNode }) {
  const ctx = useContext(TabsCtx)!;
  if (ctx.value !== id) return null;
  return <div className="fade-in">{children}</div>;
}

// ─── Modal ───────────────────────────────────────────────────────────────────

export function Modal({
  open, onClose, title, desc, children, footer, width = "max-w-lg",
}: {
  open: boolean; onClose: () => void; title: ReactNode; desc?: ReactNode;
  children: ReactNode; footer?: ReactNode; width?: string;
}) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/60 p-4 py-[8vh] backdrop-blur-sm">
      {/* Backdrop click closes; the panel stops propagation so inner clicks don't. */}
      <div className="absolute inset-0" onClick={onClose} />
      <div className={cn(
        "scale-in relative w-full overflow-hidden rounded-2xl border border-white/[0.1] bg-[#101012] shadow-2xl shadow-black/70",
        width,
      )}>
        <div className="flex items-start gap-3 border-b border-white/[0.07] px-5 py-4">
          <div className="min-w-0 flex-1">
            <h2 className="text-[15px] font-semibold text-white">{title}</h2>
            {desc && <p className="mt-0.5 text-xs text-white/40">{desc}</p>}
          </div>
          <button
            onClick={onClose}
            className="rounded-lg p-1 text-white/30 transition-colors hover:bg-white/[0.08] hover:text-white/70"
          >
            <X className="size-4" />
          </button>
        </div>
        <div className="max-h-[65vh] overflow-y-auto px-5 py-4">{children}</div>
        {footer && (
          <div className="flex items-center justify-end gap-2 border-t border-white/[0.07] bg-white/[0.02] px-5 py-3">
            {footer}
          </div>
        )}
      </div>
    </div>
  );
}

// ─── ConfirmModal ────────────────────────────────────────────────────────────

/**
 * Destructive confirmation. When `confirmText` is set the action stays disabled until the
 * user types it — reserved for deletes that can't be undone and cost something to redo
 * (generated frames are paid quota, not just disk).
 */
export function ConfirmModal({
  open, onClose, onConfirm, title, danger = true, confirmLabel = "Delete",
  confirmText, loading, children,
}: {
  open: boolean; onClose: () => void; onConfirm: () => void;
  title: ReactNode; danger?: boolean; confirmLabel?: string;
  confirmText?: string; loading?: boolean; children: ReactNode;
}) {
  const [typed, setTyped] = useState("");

  // Reset the challenge box each time the dialog opens, or a previous answer would carry over.
  useEffect(() => { if (open) setTyped(""); }, [open]);

  const ready = !confirmText || typed.trim() === confirmText;

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={title}
      width="max-w-md"
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button
            variant={danger ? "danger" : "primary"}
            disabled={!ready}
            loading={loading}
            onClick={onConfirm}
          >
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="space-y-3.5">
        {children}
        {confirmText && (
          <Field label={<>Type <code className="rounded bg-white/[0.08] px-1 font-mono text-[11px] text-amber-300">{confirmText}</code> to confirm</>}>
            <input
              className={cn(inputCls, "font-mono text-xs")}
              value={typed}
              autoComplete="off"
              spellCheck={false}
              onChange={(e) => setTyped(e.target.value)}
            />
          </Field>
        )}
      </div>
    </Modal>
  );
}

// ─── StatTile ────────────────────────────────────────────────────────────────

export function StatTile({
  label, value, sub, icon, tone = "base",
}: {
  label: ReactNode; value: ReactNode; sub?: ReactNode; icon?: ReactNode;
  tone?: "base" | "amber" | "emerald";
}) {
  return (
    <div className="rounded-xl border border-white/[0.07] bg-white/[0.02] p-4">
      <div className="flex items-center gap-2">
        {icon && (
          <span className={cn(
            "grid size-6 place-items-center rounded-md",
            tone === "amber" ? "bg-amber-500/12 text-amber-400"
              : tone === "emerald" ? "bg-emerald-500/12 text-emerald-400"
              : "bg-white/[0.05] text-white/40",
          )}>
            {icon}
          </span>
        )}
        <span className="text-[11px] font-medium uppercase tracking-wider text-white/35">{label}</span>
      </div>
      <div className="mt-2 text-2xl font-semibold tabular-nums tracking-tight text-white">{value}</div>
      {sub && <div className="mt-0.5 text-[11px] text-white/30">{sub}</div>}
    </div>
  );
}

// ─── Progress ────────────────────────────────────────────────────────────────

export function Progress({
  value, total, indeterminate, className,
}: { value: number; total: number; indeterminate?: boolean; className?: string }) {
  const pct = total > 0 ? Math.round((value / total) * 100) : 0;
  return (
    <div className={cn("relative h-1 overflow-hidden rounded-full bg-white/[0.08]", indeterminate && "sweep", className)}>
      {!indeterminate && (
        <div
          className="h-full rounded-full bg-amber-500 transition-all duration-500"
          style={{ width: `${pct}%` }}
        />
      )}
    </div>
  );
}

// ─── EmptyState ──────────────────────────────────────────────────────────────

export function EmptyState({
  icon, title, desc, action,
}: { icon?: ReactNode; title: ReactNode; desc?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-3 rounded-xl border border-dashed border-white/[0.09] px-6 py-16 text-center">
      {icon && (
        <div className="grid size-14 place-items-center rounded-2xl border border-white/[0.07] bg-white/[0.02] text-white/20">
          {icon}
        </div>
      )}
      <div>
        <p className="text-sm font-medium text-white/70">{title}</p>
        {desc && <p className="mx-auto mt-1 max-w-sm text-xs leading-relaxed text-white/35">{desc}</p>}
      </div>
      {action}
    </div>
  );
}

// ─── Skeleton ────────────────────────────────────────────────────────────────

export function Skeleton({ className }: { className?: string }) {
  return <div className={cn("shimmer rounded-lg", className)} />;
}

// ─── Callout ─────────────────────────────────────────────────────────────────

const callout = cva("flex gap-2.5 rounded-lg border px-3 py-2.5 text-xs leading-relaxed", {
  variants: {
    tone: {
      info: "border-sky-500/25 bg-sky-500/[0.06] text-sky-200/80",
      warn: "border-amber-500/25 bg-amber-500/[0.06] text-amber-200/80",
      err:  "border-red-500/25 bg-red-500/[0.06] text-red-200/80",
    },
  },
  defaultVariants: { tone: "info" },
});

export function Callout({
  tone, title, children,
}: VariantProps<typeof callout> & { title?: ReactNode; children: ReactNode }) {
  const Icon = tone === "warn" || tone === "err" ? AlertTriangle : Info;
  return (
    <div className={callout({ tone })}>
      <Icon className="mt-0.5 size-3.5 shrink-0" />
      <div className="min-w-0">
        {title && <p className="mb-0.5 font-semibold">{title}</p>}
        <div className="[&_code]:rounded [&_code]:bg-black/30 [&_code]:px-1 [&_code]:py-0.5 [&_code]:font-mono [&_code]:text-[11px]">
          {children}
        </div>
      </div>
    </div>
  );
}

// ─── Steps (numbered instructions) ───────────────────────────────────────────

export function Steps({ children }: { children: ReactNode }) {
  return <ol className="m-0 list-none space-y-3 p-0">{children}</ol>;
}

export function Step({ n, title, children }: { n: number; title: ReactNode; children?: ReactNode }) {
  return (
    <li className="flex gap-3">
      <span className="grid size-5 shrink-0 place-items-center rounded-full border border-white/[0.12] bg-white/[0.04] text-[10px] font-bold tabular-nums text-white/50">
        {n}
      </span>
      <div className="min-w-0 flex-1 pt-0.5">
        <p className="text-xs font-medium text-white/80">{title}</p>
        {children && (
          <div className="mt-1 space-y-1.5 text-[11px] leading-relaxed text-white/45
                          [&_a]:text-amber-400 [&_a]:underline [&_a]:decoration-amber-400/30
                          [&_code]:rounded [&_code]:bg-white/[0.07] [&_code]:px-1 [&_code]:py-0.5 [&_code]:font-mono [&_code]:text-[10px] [&_code]:text-amber-300/90">
            {children}
          </div>
        )}
      </div>
    </li>
  );
}

// ─── CopyButton ──────────────────────────────────────────────────────────────

export function CopyButton({ text, label = "Copy" }: { text: string; label?: string }) {
  const [done, setDone] = useState(false);
  return (
    <Button
      size="xs"
      variant="ghost"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setDone(true);
          setTimeout(() => setDone(false), 1400);
        } catch {
          // Clipboard can be blocked (insecure origin / denied permission) — the value is
          // visible on screen either way, so failing silently is fine here.
        }
      }}
    >
      {done ? <Check className="size-3" /> : <Copy className="size-3" />}
      {done ? "Copied" : label}
    </Button>
  );
}

// ─── Toast ───────────────────────────────────────────────────────────────────

export function Toast({
  show, tone = "ok", children,
}: { show: boolean; tone?: "ok" | "err"; children: ReactNode }) {
  if (!show) return null;
  return (
    <div className={cn(
      "fade-in fixed bottom-5 left-1/2 z-[60] -translate-x-1/2 rounded-lg border px-4 py-2.5 text-sm shadow-2xl shadow-black/50",
      tone === "ok"
        ? "border-emerald-500/30 bg-emerald-950/90 text-emerald-300"
        : "border-red-500/30 bg-red-950/90 text-red-300",
    )}>
      <span className="flex items-center gap-2">
        {tone === "ok" ? <Check className="size-3.5" /> : <AlertTriangle className="size-3.5" />}
        {children}
      </span>
    </div>
  );
}

// ─── KeyValue row (read-only detail display) ─────────────────────────────────

export function KeyValue({ k, v }: { k: ReactNode; v: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b border-white/[0.04] py-1.5 last:border-0">
      <span className="text-[11px] text-white/35">{k}</span>
      <span className="min-w-0 truncate text-right text-xs text-white/70">{v}</span>
    </div>
  );
}
