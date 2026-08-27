import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { loadContent } from "../content/load-content.js";
import { simulateDays } from "../simulation/simulate.js";
import { createWorld } from "../simulation/world.js";
import { buildVisualizationBootstrap, buildVisualizationState } from "./view-model.js";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const visualizerDirectory = path.join(projectRoot, "visualizer");

const staticFiles = new Map([
  ["/", { file: "index.html", type: "text/html; charset=utf-8" }],
  ["/app.js", { file: "app.js", type: "text/javascript; charset=utf-8" }],
  ["/map-geometry.js", { file: "map-geometry.js", type: "text/javascript; charset=utf-8" }],
  ["/styles.css", { file: "styles.css", type: "text/css; charset=utf-8" }]
]);

function json(response, statusCode, value) {
  response.writeHead(statusCode, {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store"
  });
  response.end(JSON.stringify(value));
}

export async function createVisualizerServer({ port = 4173 } = {}) {
  const content = await loadContent();
  let world = createWorld(content);

  const server = createServer(async (request, response) => {
    try {
      const url = new URL(request.url, `http://${request.headers.host ?? "127.0.0.1"}`);

      if (request.method === "GET" && url.pathname === "/api/state") {
        json(response, 200, buildVisualizationState(world, content));
        return;
      }

      if (request.method === "GET" && url.pathname === "/api/bootstrap") {
        json(response, 200, buildVisualizationBootstrap(world, content));
        return;
      }

      if (request.method === "POST" && url.pathname === "/api/step") {
        const days = Number(url.searchParams.get("days") ?? 1);
        if (!Number.isInteger(days) || days < 1 || days > 365) {
          json(response, 400, { error: "days должно быть целым числом от 1 до 365" });
          return;
        }
        simulateDays(world, content, days);
        json(response, 200, buildVisualizationState(world, content));
        return;
      }

      if (request.method === "POST" && url.pathname === "/api/reset") {
        world = createWorld(content);
        json(response, 200, buildVisualizationState(world, content));
        return;
      }

      const asset = staticFiles.get(url.pathname);
      if (request.method === "GET" && asset) {
        const body = await readFile(path.join(visualizerDirectory, asset.file));
        response.writeHead(200, {
          "content-type": asset.type,
          "cache-control": "no-store"
        });
        response.end(body);
        return;
      }

      json(response, 404, { error: "not found" });
    } catch (error) {
      json(response, 500, { error: error.message });
    }
  });

  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", resolve);
  });

  return {
    server,
    url: `http://127.0.0.1:${port}`,
    close: () => new Promise((resolve, reject) => server.close((error) => error ? reject(error) : resolve()))
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const port = Number(process.env.WORLDGEN_VISUALIZER_PORT ?? 4173);
  const running = await createVisualizerServer({ port });
  console.log(`WorldGen visualizer: ${running.url}`);
  console.log("Press Ctrl+C to stop.");
}
