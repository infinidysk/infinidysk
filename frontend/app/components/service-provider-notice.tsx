import { Button, Icon, Modal } from "~/components/ui";
import type { ServiceProviderConfig } from "~/utils/service-provider";

export type ServiceProviderNoticeProps = {
  open: boolean;
  serviceProvider: ServiceProviderConfig;
  onClose: () => void;
};

export function ServiceProviderNotice({
  open,
  serviceProvider,
  onClose,
}: ServiceProviderNoticeProps) {
  return (
    <Modal
      open={open}
      title="Feature Not Available"
      onClose={onClose}
      className="max-w-lg"
      footer={
        <Button onClick={onClose}>
          Close
        </Button>
      }
    >
      <div className="flex flex-col items-center gap-4 text-center">
        <Icon name="info" className="!text-[52px] text-base-content/60" />
        <p className="max-w-sm text-base-content/80">
          This feature is disabled by your service provider:{" "}
          <strong className="font-semibold text-base-content">{serviceProvider.name}</strong>.
        </p>
        <a
          href={serviceProvider.url}
          target="_blank"
          rel="noreferrer"
          className="link link-primary inline-flex items-center gap-1"
        >
          Contact {serviceProvider.name}
          <Icon name="open_in_new" className="!text-[16px]" />
        </a>
      </div>
    </Modal>
  );
}
