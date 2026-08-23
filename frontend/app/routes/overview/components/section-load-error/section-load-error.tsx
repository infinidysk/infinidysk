import { Alert, Button, Icon } from "~/components/ui";

export function SectionLoadError({ label, onRetry }: { label: string; onRetry: () => void }) {
  return (
    <Alert variant="warning" className="alert-soft items-center">
      <Icon name="error" className="shrink-0 !text-[20px]" />
      <span className="min-w-0 flex-1 text-sm">
        Could not load {label}. Check the connection and try again.
      </span>
      <Button variant="ghost" size="xsmall" onClick={onRetry}>
        Retry
      </Button>
    </Alert>
  );
}
