import { Icon } from "~/components/ui";

export function EmptyQueue() {
    return (
        <div className="hero min-h-[300px] -translate-y-5 py-8">
            <div className="hero-content">
                <div className="card bg-base-200 shadow-sm">
                    <div className="card-body items-center text-center">
                        <Icon name="celebration" className="!text-[48px] text-base-content/40" />
                        <h2 className="card-title text-lg">Empty Queue!</h2>
                        <p className="text-base-content/60 max-w-sm text-xs leading-relaxed">
                            Use the Upload NZB button above to get started.
                        </p>
                    </div>
                </div>
            </div>
        </div>
    );
}