import { isRcloneProxyWarningActive } from "../../../server/rclone-proxy-warning.server";

export function loader() {
  return Response.json({ active: isRcloneProxyWarningActive() });
}
