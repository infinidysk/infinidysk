import { useCallback } from "react";
import { Button, Icon } from "~/components/ui";

interface EmptyQueueProps {
  onUploadClicked?: () => void;
}

export function EmptyQueue(props: EmptyQueueProps) {
  const onUploadClicked = useCallback(() => {
    props.onUploadClicked?.call(null);
  }, [props.onUploadClicked]);

  return (
    <div className="hero min-h-[300px] -translate-y-5 py-8">
      <div className="hero-content">
        <div className="card bg-base-200 shadow-sm">
          <div className="card-body items-center text-center">
            <Icon name="celebration" className="!text-[48px] text-base-content/40" />
            <h2 className="card-title text-lg">Empty Queue!</h2>
            <p className="text-base-content/60 max-w-sm text-xs leading-relaxed">
              Upload an nzb file to get started.
            </p>
            <div className="card-actions justify-center">
              <Button variant="primary" size="small" onClick={onUploadClicked}>
                Upload an nzb file
              </Button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
