import { Icon } from "~/components/ui";

export type HamburgerMenuProps = {
  isOpen: boolean;
  onClick: () => void;
};

export function HamburgerMenu(props: HamburgerMenuProps) {
  return (
    <button
      type="button"
      aria-label={props.isOpen ? "Close navigation" : "Open navigation"}
      aria-expanded={props.isOpen}
      onClick={props.onClick}
      className="flex h-10 w-10 items-center justify-center rounded-full bg-base-content/5 text-base-content hover:bg-base-content/10 md:hidden"
    >
      <Icon name={props.isOpen ? "close" : "menu"} className="!text-[26px]" />
    </button>
  );
}
