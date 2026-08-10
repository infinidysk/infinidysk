import type { ReactNode } from "react"
import { useCallback } from "react"
import { useNavigate } from "react-router"
import { Icon } from "~/components/ui"
import { getExploreBreadcrumbHref } from "~/utils/path"

export type BreadcrumbProps = {
    parentDirectories: string[]
}

export function Breadcrumbs({ parentDirectories }: BreadcrumbProps): ReactNode {
    const navigate = useNavigate();
    const onClick = useCallback((index: number) => {
        void navigate(getExploreBreadcrumbHref(parentDirectories, index));
    }, [parentDirectories, navigate]);

    return (
        <nav aria-label="Breadcrumb" className="breadcrumbs mb-6 text-sm md:mb-8">
            <ul>
                <li>
                    <button
                        type="button"
                        className="flex items-center gap-1"
                        onClick={() => onClick(-1)}
                    >
                        <Icon name="home" filled className="!text-[18px]" />
                        {parentDirectories.length === 0 && <span>Home</span>}
                    </button>
                </li>
                {parentDirectories.map((parentDirectory, index) => (
                    <li key={index}>
                        <button
                            type="button"
                            className="max-w-full break-all text-left"
                            onClick={() => onClick(index)}
                            aria-current={index === parentDirectories.length - 1 ? "page" : undefined}
                        >
                            {parentDirectory}
                        </button>
                    </li>
                ))}
            </ul>
        </nav>
    );
}