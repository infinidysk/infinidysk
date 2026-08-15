import { Alert, Icon } from "~/components/ui";

/**
 * Persistent (non-dismissible) notice when RESET_ADMIN_PASSWORD is set in the
 * container environment. The root loader forwards the env flag here; the backend
 * deletes the admin account on startup while this variable remains set.
 */
export function ResetAdminPasswordBanner({
  isResetAdminPasswordSet,
}: {
  isResetAdminPasswordSet: boolean;
}) {
  if (!isResetAdminPasswordSet) return null;

  return (
    <Alert
      variant="danger"
      className="mb-4 grid-cols-[auto_minmax(0,1fr)] items-start gap-3 border border-error-content/15 text-sm shadow-sm"
    >
      <Icon name="lock_reset" className="mt-0.5 shrink-0 !text-[22px]" />
      <div className="min-w-0">
        <p className="font-semibold">Admin password reset is armed</p>
        <p className="mt-0.5 text-error-content/80">
          <code>RESET_ADMIN_PASSWORD</code> is set in your environment. The backend deletes the
          admin account on every startup while this remains enabled. Remove this variable from
          your Compose file or environment before the next restart.
        </p>
      </div>
    </Alert>
  );
}
