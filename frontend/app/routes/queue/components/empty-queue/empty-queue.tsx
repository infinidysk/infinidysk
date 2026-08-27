import { Icon } from "~/components/ui";

export function EmptyQueue() {
  return (
    <div className="hero min-h-[300px] -translate-y-5 py-8">
      <div className="hero-content">
        <div className="card bg-base-200 shadow-sm">
          <div className="card-body items-center text-center">
            <Icon name="celebration" className="!text-[48px] text-base-content/40" />
            <h2 className="card-title text-lg">Queue is empty</h2>
            <p className="text-base-content/60 max-w-sm text-xs leading-relaxed">
              Upload an NZB above, or send jobs from Sonarr or Radarr using InfiniDysk as the
              download client. New items appear here until they finish importing.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
