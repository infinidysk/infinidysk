import { formatBytes, formatNumber } from "../../utils/format";

export type CatalogueBlockProps = {
    catalogue: {
        fileCount: number,
        totalBytes: number,
        largestFileBytes: number,
        addedLast7Days: number,
    },
}

export function CatalogueBlock({ catalogue }: CatalogueBlockProps) {
    return (
        <section className="card w-full border border-base-content/10 bg-base-100 shadow-sm">
            <div className="card-body gap-3 p-4">
                <div>
                    <h3 className="card-title text-base">Catalogue</h3>
                    <p className="text-xs text-base-content/50">Your mounted library</p>
                </div>

                <div className="stats stats-vertical w-full border border-base-content/10 bg-base-200 shadow sm:stats-horizontal">
                    <Stat label="Files" value={formatNumber(catalogue.fileCount)} />
                    <Stat label="Total size" value={formatBytes(catalogue.totalBytes)} />
                    <Stat label="Largest file" value={formatBytes(catalogue.largestFileBytes)} />
                    <Stat
                        label="Added 7d"
                        value={formatNumber(catalogue.addedLast7Days)}
                        accent={catalogue.addedLast7Days > 0 ? "good" : undefined}
                    />
                </div>
            </div>
        </section>
    );
}

function Stat({ label, value, accent }: { label: string, value: string, accent?: "good" | undefined }) {
    return (
        <div className="stat py-3">
            <div className="stat-title text-xs">{label}</div>
            <div className={`stat-value font-mono text-xl md:text-2xl ${accent === "good" ? "text-success" : ""}`}>
                {value}
            </div>
        </div>
    );
}
