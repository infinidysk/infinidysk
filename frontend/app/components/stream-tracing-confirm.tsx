import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";

export type DiscardTracesConfirmModalProps = {
  show: boolean;
  eventCount: number;
  sessionCount: number;
  onCancel: () => void;
  onConfirm: () => void;
};

export function DiscardTracesConfirmModal({
  show,
  eventCount,
  sessionCount,
  onCancel,
  onConfirm,
}: DiscardTracesConfirmModalProps) {
  const counts = `${eventCount.toLocaleString()} events across ${sessionCount.toLocaleString()} sessions`;

  return (
    <ConfirmModal
      show={show}
      title="Discard captured traces?"
      message={
        <>
          {counts} are held in memory for a support pack. Discarding frees that memory now and the
          traces cannot be recovered. Generate a support pack first if you have not already.
        </>
      }
      cancelText="Keep traces"
      confirmText="Discard traces"
      onCancel={onCancel}
      onConfirm={onConfirm}
    />
  );
}
