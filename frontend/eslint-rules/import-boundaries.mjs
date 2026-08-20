import { fileURLToPath } from "node:url";
import path from "node:path";

const FRONTEND_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const APP_ROOT = path.join(FRONTEND_ROOT, "app");

function toPosix(value) {
  return value.split(path.sep).join("/");
}

function stripQuery(source) {
  const q = source.indexOf("?");
  return q >= 0 ? source.slice(0, q) : source;
}

export function resolveImportedPath(fromFile, source, appRoot = APP_ROOT) {
  const specifier = stripQuery(source);
  if (specifier.startsWith("~/")) {
    return path.resolve(appRoot, specifier.slice(2));
  }
  if (specifier.startsWith(".")) {
    return path.resolve(path.dirname(fromFile), specifier);
  }
  return null;
}

function relativeToApp(absPath, appRoot = APP_ROOT) {
  const rel = toPosix(path.relative(appRoot, absPath));
  if (rel.startsWith("..")) return null;
  return rel;
}

function routeFeature(relFromApp) {
  if (!relFromApp) return null;
  const parts = relFromApp.split("/");
  if (parts[0] !== "routes" || parts.length < 2) return null;
  return parts[1] ?? null;
}

function isSharedImporter(relFromApp) {
  return (
    relFromApp.startsWith("clients/") ||
    relFromApp.startsWith("auth/") ||
    relFromApp.startsWith("components/") ||
    relFromApp.startsWith("navigation/") ||
    relFromApp.startsWith("utils/")
  );
}

function isExceptionImporter(fromFile, frontendRoot = FRONTEND_ROOT) {
  const rel = toPosix(path.relative(frontendRoot, fromFile));
  if (rel === "app/routes.ts") return true;
  if (rel === "server.ts" || rel.startsWith("server/")) return true;
  return false;
}

/**
 * @returns {{ message: string } | null}
 */
export function describeImportViolation(fromFile, source, options = {}) {
  const appRoot = options.appRoot ?? APP_ROOT;
  const frontendRoot = options.frontendRoot ?? FRONTEND_ROOT;
  if (isExceptionImporter(fromFile, frontendRoot)) return null;

  const importedAbs = resolveImportedPath(fromFile, source, appRoot);
  if (!importedAbs) return null;

  const fromRel = relativeToApp(fromFile, appRoot);
  const importedRel = relativeToApp(importedAbs, appRoot);
  if (!fromRel || !importedRel) return null;

  const importedRoute = importedRel.startsWith("routes/");
  if (isSharedImporter(fromRel) && importedRoute) {
    return {
      message:
        `${fromRel} must not import route module ${importedRel}. ` +
        `Shared clients/auth/components/navigation/utils cannot depend on app/routes.`,
    };
  }

  const fromFeature = routeFeature(fromRel);
  const toFeature = routeFeature(importedRel);
  if (fromFeature && toFeature && fromFeature !== toFeature) {
    return {
      message:
        `${fromRel} must not import ${importedRel} ` +
        `(route feature '${fromFeature}' cannot import '${toFeature}'). ` +
        `Move shared UI to app/components, app/navigation, or app/utils.`,
    };
  }

  return null;
}

export const importBoundariesPlugin = {
  meta: {
    name: "import-boundaries",
    version: "1.0.0",
  },
  rules: {
    "no-cross-feature-imports": {
      meta: {
        type: "problem",
        docs: {
          description: "Prevent cross-route and shared-to-route imports, including ~/ aliases.",
        },
        schema: [],
        messages: {
          forbidden: "{{detail}}",
        },
      },
      create(context) {
        function checkSource(sourceNode) {
          const source = sourceNode?.value;
          if (typeof source !== "string") return;
          const violation = describeImportViolation(context.filename, source);
          if (!violation) return;
          context.report({
            node: sourceNode,
            messageId: "forbidden",
            data: { detail: violation.message },
          });
        }

        return {
          ImportDeclaration(node) {
            checkSource(node.source);
          },
          ExportNamedDeclaration(node) {
            if (node.source) checkSource(node.source);
          },
          ExportAllDeclaration(node) {
            checkSource(node.source);
          },
          ImportExpression(node) {
            checkSource(node.source);
          },
        };
      },
    },
  },
};
