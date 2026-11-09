import { type ComponentProps, type ReactNode, type Ref, useEffect, useId, useState } from "react";
import { ChevronLeft, ChevronRight, CircleAlert, Ellipsis, Info, Loader2 } from "lucide-react";
import { RadioGroup } from "radix-ui";
import { cn } from "@/lib/utils";
import { FieldContext } from "@/lib/field";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { Schemas } from "./api/client";

/** Sets the browser title, most specific part first, and restores the previous one on unmount. */
export function useTitle(...parts: (string | undefined)[]) {
  const title = [...parts.filter(Boolean), "infinidysk"].join(" | ");
  useEffect(() => {
    const previous = document.title;
    document.title = title;
    return () => {
      document.title = previous;
    };
  }, [title]);
}

/** `tab` overrides the browser title parts, which default to the page title. */
export function PageHeader({
  title,
  description,
  tab,
  children,
}: {
  title: string;
  description?: string;
  tab?: (string | undefined)[];
  children?: ReactNode;
}) {
  useTitle(...(tab ?? [title]));
  return (
    <header className="mb-6 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 className="text-xl font-medium tracking-tight">{title}</h1>
        {description && <p className="text-muted-foreground mt-1">{description}</p>}
      </div>
      {children && <div className="flex flex-wrap items-center gap-2">{children}</div>}
    </header>
  );
}

export function Loading() {
  return (
    <div className="space-y-2" aria-busy="true">
      <Skeleton className="h-9 w-full" />
      <Skeleton className="h-9 w-full" />
      <Skeleton className="h-9 w-2/3" />
    </div>
  );
}

export function Failure({ error, className }: { error: Error; className?: string }) {
  return (
    <Alert variant="destructive" className={className}>
      <CircleAlert />
      <AlertDescription>{error.message}</AlertDescription>
    </Alert>
  );
}

export function Empty({ title, action, children }: { title: string; action?: ReactNode; children?: ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed px-6 py-16 text-center">
      <p className="font-medium">{title}</p>
      {children && <p className="text-muted-foreground mt-1">{children}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}

/** A settings category: title, description and header actions above its fields. */
export function SettingsSection({
  title,
  description,
  actions,
  className,
  children,
}: {
  title: string;
  description: ReactNode;
  actions?: ReactNode;
  className?: string;
  children: ReactNode;
}) {
  return (
    <section className={cn("max-w-3xl", className)}>
      <header className="mb-6 flex flex-wrap items-end justify-between gap-3 border-b pb-5">
        <div>
          <h2 className="text-lg font-medium tracking-tight">{title}</h2>
          <p className="text-muted-foreground mt-1">{description}</p>
        </div>
        {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
      </header>
      <div className="space-y-6">{children}</div>
    </section>
  );
}

/**
 * A label, its control, and an optional error and hint, both linked to the control.
 * `horizontal` puts the label and hint beside the control from `sm`; `row` puts a short control in a right-hand
 * column from `md`, for settings panels.
 */
export function Field({
  id,
  label,
  hint,
  error,
  layout,
  className,
  children,
}: {
  id?: string;
  label: ReactNode;
  hint?: ReactNode;
  error?: ReactNode;
  layout?: "horizontal" | "row";
  className?: string;
  children: ReactNode;
}) {
  const auto = useId();
  const base = id ?? auto;
  const describedBy = [error && `${base}-error`, hint && `${base}-hint`].filter(Boolean).join(" ") || undefined;
  const control = (
    <FieldContext value={{ "aria-describedby": describedBy, "aria-invalid": error ? true : undefined }}>{children}</FieldContext>
  );
  const errorText = error && (
    <p id={`${base}-error`} className="text-destructive text-xs">
      {error}
    </p>
  );
  const hintText = hint && (
    <p id={`${base}-hint`} className="text-muted-foreground text-xs">
      {hint}
    </p>
  );
  if (layout)
    return (
      <div
        className={cn(
          "grid gap-1.5",
          layout === "row" ? "md:grid-cols-[minmax(0,1fr)_40%] md:gap-x-6" : "sm:grid-cols-[1fr_2fr] sm:gap-x-4",
          className,
        )}
      >
        <div className={cn("space-y-1", layout === "row" ? "md:pt-1.5" : "sm:pt-1.5")}>
          <Label htmlFor={id}>{label}</Label>
          {hintText}
        </div>
        <div className="min-w-0 space-y-1.5">
          {control}
          {errorText}
        </div>
      </div>
    );
  return (
    <div className={cn("space-y-1.5", className)}>
      <Label htmlFor={id}>{label}</Label>
      {control}
      {errorText}
      {hintText}
    </div>
  );
}

/** The visible reason a setting is disabled: an environment variable sets it. */
export function Locked({ locked }: { locked: boolean }) {
  return locked ? (
    <Badge variant="outline" className="text-muted-foreground ml-1">
      Set by environment
    </Badge>
  ) : null;
}

/** Asks "Discard changes?" before throwing edits away. */
export function DiscardDialog({
  open,
  description = "Your unsaved changes will be lost.",
  onKeep,
  onDiscard,
}: {
  open: boolean;
  description?: ReactNode;
  onKeep: () => void;
  onDiscard: () => void;
}) {
  return (
    <AlertDialog open={open} onOpenChange={(next) => !next && onKeep()}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Discard changes?</AlertDialogTitle>
          <AlertDialogDescription>{description}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Keep editing</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={onDiscard}>
            Discard changes
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

/** Routes every way of closing an editor through one check that asks before dropping edits. */
export function useDiscardGuard(dirty: boolean, onClose: () => void) {
  const [asking, setAsking] = useState(false);
  return {
    requestClose: () => (dirty ? setAsking(true) : onClose()),
    dialog: (
      <DiscardDialog
        open={asking}
        onKeep={() => setAsking(false)}
        onDiscard={() => {
          setAsking(false);
          onClose();
        }}
      />
    ),
  };
}

/** A right-side sheet holding one form, with Cancel and the submit button in a sticky footer. */
export function EditorSheet({
  open,
  title,
  description,
  dirty,
  saving,
  error,
  notice,
  submit,
  readOnly,
  onSubmit,
  onClose,
  children,
}: {
  open: boolean;
  title: string;
  description?: ReactNode;
  dirty: boolean;
  saving: boolean;
  error?: Error | null;
  /** Shown above the footer like `error`, for problems that come with their own action. */
  notice?: ReactNode;
  submit: string;
  /** Shows the form without a submit button, for values set by the environment. */
  readOnly?: boolean;
  onSubmit: () => void;
  onClose: () => void;
  children: ReactNode;
}) {
  const guard = useDiscardGuard(dirty, onClose);
  return (
    <>
      <Sheet open={open} onOpenChange={(next) => !next && guard.requestClose()}>
        <SheetContent className="gap-0 data-[side=right]:w-full data-[side=right]:sm:max-w-xl">
          <form
            className="flex min-h-0 flex-1 flex-col"
            onSubmit={(e) => {
              e.preventDefault();
              onSubmit();
            }}
          >
            <SheetHeader className="border-b pr-12">
              <SheetTitle>{title}</SheetTitle>
              {description && <SheetDescription>{description}</SheetDescription>}
            </SheetHeader>
            <div className="flex-1 space-y-6 overflow-y-auto p-4">{children}</div>
            {error ? (
              <div className="border-t p-4">
                <Failure error={error} />
              </div>
            ) : (
              notice && <div className="border-t p-4">{notice}</div>
            )}
            <SheetFooter className="flex-row justify-end border-t">
              <Button type="button" variant="outline" onClick={guard.requestClose}>
                {readOnly ? "Close" : "Cancel"}
              </Button>
              {!readOnly && (
                <Button disabled={saving}>
                  {saving && <Loader2 className="animate-spin" />}
                  {submit}
                </Button>
              )}
            </SheetFooter>
          </form>
        </SheetContent>
      </Sheet>
      {guard.dialog}
    </>
  );
}

/** Related fields under a meta-label heading. */
export function FieldGroup({ title, children }: { title: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="space-y-4">
      <h3 id={id} className={metaLabel}>
        {title}
      </h3>
      {children}
    </section>
  );
}

const metaLabel = "text-muted-foreground text-xs font-medium tracking-wide uppercase";

/** A settings group: one bordered panel whose rows are split by dividers, under an optional meta-label heading. */
export function SettingsPanel({ title, children }: { title?: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={title && id} className="space-y-2">
      {title && (
        <h3 id={id} className={metaLabel}>
          {title}
        </h3>
      )}
      <div className="bg-card divide-y rounded-lg border *:p-4">{children}</div>
    </section>
  );
}

/** Settings most people never change, behind a disclosure. */
export function AdvancedSettings({ children }: { children: ReactNode }) {
  return (
    <Collapsible className="group/advanced space-y-4">
      <CollapsibleTrigger className="text-muted-foreground hover:text-foreground relative flex items-center gap-1 rounded-sm text-xs font-medium tracking-wide uppercase outline-none after:absolute after:-inset-x-2 after:-inset-y-3 focus-visible:ring-3 focus-visible:ring-ring/50">
        <ChevronRight className="size-3.5 transition-transform group-data-[state=open]/advanced:rotate-90" />
        Advanced settings
      </CollapsibleTrigger>
      <CollapsibleContent className="space-y-4">{children}</CollapsibleContent>
    </Collapsible>
  );
}

/** A switch row: label and hint on the left, the switch on the right. */
export function Toggle({
  id,
  label,
  hint,
  checked,
  disabled,
  onChange,
}: {
  id: string;
  label: ReactNode;
  hint?: ReactNode;
  checked: boolean;
  disabled?: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <div className="flex items-start justify-between gap-4">
      <div>
        <Label htmlFor={id}>{label}</Label>
        {hint && (
          <p id={`${id}-hint`} className="text-muted-foreground mt-1 text-xs">
            {hint}
          </p>
        )}
      </div>
      <Switch
        id={id}
        className="mt-px"
        aria-describedby={hint ? `${id}-hint` : undefined}
        checked={checked}
        disabled={disabled}
        onCheckedChange={onChange}
      />
    </div>
  );
}

/** An info icon that explains something on hover, focus or tap. */
export function InfoTip({ label, children }: { label: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <Tooltip open={open} onOpenChange={setOpen}>
      <TooltipTrigger
        asChild
        onClick={(e) => {
          // Touch never hovers; without this the tooltip's own click handler closes it.
          e.preventDefault();
          setOpen(true);
        }}
      >
        <button
          type="button"
          aria-label={label}
          className="text-muted-foreground hover:text-foreground relative inline-flex rounded-sm align-middle after:absolute after:-inset-2"
        >
          <Info className="size-3.5" />
        </button>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs text-pretty">{children}</TooltipContent>
    </Tooltip>
  );
}

/** A selectable card: an option card, or an action card that starts a change. */
export const choiceCard = cn(
  "bg-card flex flex-col justify-start rounded-lg border p-3 text-left transition-colors disabled:opacity-50",
  "focus-visible:ring-ring/50 outline-none focus-visible:ring-3",
);

/** One radio group of cards: one Tab stop, arrow keys move and select. */
export function OptionCards<T extends string>({
  label,
  value,
  options,
  disabled,
  className,
  onChange,
}: {
  label: string;
  value: T;
  options: { value: T; title: ReactNode; description: ReactNode; meta?: ReactNode }[];
  disabled?: boolean;
  className?: string;
  onChange: (value: T) => void;
}) {
  return (
    <RadioGroup.Root
      aria-label={label}
      value={value}
      disabled={disabled}
      onValueChange={(v) => onChange(v as T)}
      className={cn("grid gap-2 sm:grid-cols-3", className)}
    >
      {options.map((o) => (
        <RadioGroup.Item
          key={o.value}
          value={o.value}
          className={cn(
            choiceCard,
            "hover:bg-accent/60 data-[state=checked]:border-primary data-[state=checked]:ring-primary data-[state=checked]:hover:bg-card data-[state=checked]:ring-1",
          )}
        >
          <span className="flex items-center gap-1.5 font-medium">{o.title}</span>
          <span className="text-muted-foreground mt-1 block text-xs">{o.description}</span>
          {o.meta && <span className="text-muted-foreground mt-2 block font-mono text-xs tabular-nums">{o.meta}</span>}
        </RadioGroup.Item>
      ))}
    </RadioGroup.Root>
  );
}

/** Bordered container for a table, so tables read as one panel. */
export function Panel({ children, className, ref }: { children: ReactNode; className?: string; ref?: Ref<HTMLDivElement> }) {
  return (
    <div ref={ref} className={cn("bg-card overflow-hidden rounded-lg border", className)}>
      {children}
    </div>
  );
}

export type Tone = "success" | "warning" | "destructive" | "primary" | "muted";

const dots: Record<Tone, string> = {
  success: "bg-success",
  warning: "bg-warning",
  destructive: "bg-destructive",
  primary: "bg-primary",
  muted: "bg-muted-foreground/50",
};

/** A coloured dot followed by a label; `compact` sets the label in `text-xs` below `sm`. */
export function Status({ tone, className, children, compact }: { tone: Tone; className?: string; children: ReactNode; compact?: boolean }) {
  return (
    <span className={cn("inline-flex items-center whitespace-nowrap", compact ? "gap-1.5 text-xs sm:gap-2 sm:text-sm" : "gap-2", className)}>
      <span className={cn("size-2 shrink-0 rounded-full", dots[tone])} aria-hidden />
      {children}
    </span>
  );
}

/** Episode tags win over years, so a year in an episode title stays muted; greedy, so the last tag wins. */
const RELEASE_TAGS = [
  /^(.*[. _]S\d{1,3}(?:E\d{1,4})*)([. _].*)$/i,
  /^(.*[. _](?:(?:19|20)\d\d(?:[. _-]\d\d[. _-]\d\d)?|2100))([. _].*)$/,
];

/** A release name with everything after its episode tag or year muted, so the title stands out. */
export function ReleaseName({ name }: { name: string }) {
  const match = RELEASE_TAGS.map((tag) => tag.exec(name)).find(Boolean);
  if (!match) return name;
  return (
    <>
      {match[1]}
      <span className="text-muted-foreground">{match[2]}</span>
    </>
  );
}

/** Rows per page; lists fetch one extra row to know whether a next page exists. */
export const PAGE = 50;

export const healthStates: Record<Schemas["ItemHealth"], [string, Tone]> = {
  unchecked: ["Unchecked", "muted"],
  healthy: ["Healthy", "success"],
  degraded: ["Degraded", "warning"],
  suspect: ["Suspect", "warning"],
  repairing: ["Repairing", "primary"],
  hidden: ["Hidden", "primary"],
  action_needed: ["Needs action", "destructive"],
  ignored: ["Ignored", "muted"],
};

export const checkResults: Record<Schemas["CheckResult"], [string, Tone]> = {
  healthy: ["Healthy", "success"],
  degraded: ["Degraded", "warning"],
  missing: ["Missing articles", "destructive"],
  damaged: ["Broken container", "destructive"],
  inconclusive: ["Inconclusive", "muted"],
};

export const checkRepairs: Record<Schemas["CheckRepair"], string | null> = {
  none: null,
  replacement_requested: "Replacement requested",
  replaced: "Replaced",
  action_needed: "Needs action",
  deleted: "Deleted",
};

/** One health-check row: its result and, when one happened, the repair step. */
export function CheckStatus({ check }: { check: { result: Schemas["CheckResult"]; repair: Schemas["CheckRepair"] } }) {
  const [label, tone] = checkResults[check.result];
  const repair = checkRepairs[check.repair];
  return (
    <Status tone={tone}>
      {label}
      {repair && <span className="text-muted-foreground">· {repair}</span>}
    </Status>
  );
}

export function Pager({ offset, more, onChange }: { offset: number; more: boolean; onChange: (offset: number) => void }) {
  if (offset === 0 && !more) return null;
  return (
    <div className="mt-4 flex items-center justify-end gap-2">
      <span className="text-muted-foreground mr-2 tabular-nums">Page {offset / PAGE + 1}</span>
      <Button variant="outline" size="sm" disabled={offset === 0} onClick={() => onChange(Math.max(0, offset - PAGE))}>
        <ChevronLeft />
        Previous
      </Button>
      <Button variant="outline" size="sm" disabled={!more} onClick={() => onChange(offset + PAGE)}>
        Next
        <ChevronRight />
      </Button>
    </div>
  );
}

/** An icon-only button whose `label` is both its accessible name and its tooltip. */
export function IconButton({
  label,
  variant = "ghost",
  size = "icon-sm",
  ...props
}: { label: string } & Omit<ComponentProps<typeof Button>, "aria-label" | "title">) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button type="button" variant={variant} size={size} aria-label={label} {...props} />
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/** A button that asks before running `onConfirm`; with `icon`, it is an `IconButton` named `label`. */
export function ConfirmButton({
  label,
  icon,
  title,
  message,
  confirm,
  onConfirm,
  variant = "ghost",
  size = "sm",
  disabled,
}: ({ label: ReactNode; icon?: undefined } | { label: string; icon: ReactNode }) & {
  title: string;
  message: ReactNode;
  confirm: string;
  onConfirm: () => void;
  variant?: "ghost" | "outline" | "destructive";
  size?: "sm" | "icon-sm" | "icon";
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      {icon ? (
        <IconButton label={label as string} variant={variant} size={size === "sm" ? "icon-sm" : size} disabled={disabled} onClick={() => setOpen(true)}>
          {icon}
        </IconButton>
      ) : (
        <Button type="button" variant={variant} size={size} disabled={disabled} onClick={() => setOpen(true)}>
          {label}
        </Button>
      )}
      <ConfirmDialog open={open} onOpenChange={setOpen} title={title} message={message} confirm={confirm} onConfirm={onConfirm} />
    </>
  );
}

/** The dialog behind `ConfirmButton`, for confirmations opened from somewhere else, such as a menu. */
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  message,
  confirm,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  message: ReactNode;
  confirm: string;
  onConfirm: () => void;
}) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription className="break-words">{message}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={onConfirm}>
            {confirm}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

/** Confirms an irreversible bulk delete: the button stays disabled until `expected` is typed exactly. */
export function TextConfirmDialog({
  open,
  onOpenChange,
  title,
  message,
  expected,
  confirm,
  onConfirm,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  message: ReactNode;
  expected: string;
  confirm: string;
  onConfirm: () => void;
}) {
  const [typed, setTyped] = useState("");
  const id = useId();
  return (
    <AlertDialog
      open={open}
      onOpenChange={(next) => {
        if (!next) setTyped("");
        onOpenChange(next);
      }}
    >
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription className="break-words">{message}</AlertDialogDescription>
        </AlertDialogHeader>
        <div className="space-y-1.5">
          <Label htmlFor={id}>
            Type <span className="font-mono">{expected}</span> to confirm
          </Label>
          <Input id={id} value={typed} autoComplete="off" onChange={(e) => setTyped(e.target.value)} />
        </div>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" disabled={typed.trim() !== expected} onClick={onConfirm}>
            {confirm}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

/** One table-row action; `href` downloads, `confirm` asks first and marks the action destructive. */
export type RowAction = {
  label: string;
  icon: ReactNode;
  onSelect?: () => void;
  href?: string;
  disabled?: boolean;
  confirm?: { title: string; message: ReactNode; confirm: string };
};

/**
 * Row actions, most common first: up to two show as icon buttons; beyond that the first shows and the rest move
 * into an overflow menu, destructive ones last after a separator.
 */
export function RowActions({ name, actions }: { name: string; actions: (RowAction | false | null | undefined)[] }) {
  const [confirming, setConfirming] = useState<RowAction>();
  const list = actions.filter((a): a is RowAction => !!a);
  const inline = list.length <= 2 ? list : list.slice(0, 1);
  const rest = list.slice(inline.length);
  const safe = rest.filter((a) => !a.confirm);
  const destructive = rest.filter((a) => a.confirm);
  const run = (a: RowAction) => (a.confirm ? setConfirming(a) : a.onSelect?.());
  const item = (a: RowAction) =>
    a.href ? (
      <DropdownMenuItem key={a.label} asChild disabled={a.disabled}>
        <a href={a.href} download>
          {a.icon}
          {a.label}
        </a>
      </DropdownMenuItem>
    ) : (
      <DropdownMenuItem key={a.label} variant={a.confirm ? "destructive" : "default"} disabled={a.disabled} onSelect={() => run(a)}>
        {a.icon}
        {a.label}
      </DropdownMenuItem>
    );
  return (
    <div className="flex justify-end gap-2">
      {inline.map((a) =>
        a.href ? (
          <IconButton key={a.label} label={a.label} disabled={a.disabled} asChild>
            <a href={a.href} download>
              {a.icon}
            </a>
          </IconButton>
        ) : (
          <IconButton key={a.label} label={a.label} disabled={a.disabled} onClick={() => run(a)}>
            {a.icon}
          </IconButton>
        ),
      )}
      {rest.length > 0 && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <IconButton label={`More actions for ${name}`}>
              <Ellipsis />
            </IconButton>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {safe.map(item)}
            {safe.length > 0 && destructive.length > 0 && <DropdownMenuSeparator />}
            {destructive.map(item)}
          </DropdownMenuContent>
        </DropdownMenu>
      )}
      {confirming?.confirm && (
        <ConfirmDialog
          open
          onOpenChange={(open) => !open && setConfirming(undefined)}
          {...confirming.confirm}
          onConfirm={() => confirming.onSelect?.()}
        />
      )}
    </div>
  );
}
