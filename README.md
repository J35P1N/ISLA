# ISLA

**Indespensible Shopping List Application**

.NET 10 Blazor Web App based on the standard template, with server interactivity and Tailwind CSS 4. The application has a dedicated Recipes section for managing a shared household recipe collection.

## Screenshots

Screenshots use sample household data.

### Weekly planner

Plan the week's evening meals and reuse leftovers from earlier cooks.

![ISLA weekly planner showing planned meals and leftovers](docs/screenshots/weekly-planner.png)

### Recipes

Keep a shared collection of recipes and their ingredients.

![ISLA recipe collection with sample household recipes](docs/screenshots/recipes.png)

### Shopping list

Combine recipe ingredients, check the pantry, and track estimated costs.

![ISLA shopping list with quantities and price estimates](docs/screenshots/shopping-list.png)

### In-store mode on mobile

Tick off purchases and record prices while shopping.

<img src="docs/screenshots/in-store-mobile.png" alt="ISLA mobile in-store checklist with price fields" width="360" />

## Layout

```text
ISLA.sln        Solution
src/ISLA/       Blazor application
  Components/              Layout, navigation, and Razor pages
  Styles/app.css           Tailwind entry point and Blazor state styles
  wwwroot/                 Static assets (app.css is generated)
  package.json             CSS build and watch commands
Dockerfile                 Multi-stage Node/.NET build and non-root runtime
compose.yaml               Local production container
```

## Run locally

Install Git, the .NET 10 SDK, and Node.js 24 LTS (including npm).

Clone the repository:

```sh
git clone https://github.com/J35P1N/ISLA---Indespensible-Shopping-List-Application.git ISLA
cd ISLA
```

Build and launch the application from the repository root:

```sh
dotnet build ISLA.sln
dotnet run --project src/ISLA --launch-profile http
```

Open http://localhost:5080. The .NET build automatically installs locked npm dependencies when missing and compiles Tailwind before static assets are discovered. Commit `package-lock.json` when dependencies change; run `npm ci --prefix src/ISLA` to synchronize an existing install.

For HTTPS, trust a development certificate with `dotnet dev-certs https --trust`, then select `--launch-profile https` (https://localhost:7080).

For development with automatic updates, run these in separate terminals:

```sh
npm run css:watch --prefix src/ISLA
dotnet watch --project src/ISLA --launch-profile http
```

Edit `Styles/app.css` for theme/global styles and use Tailwind utilities directly in Razor components. Class scanning is explicitly scoped to `Components/`; use complete class names so Tailwind can discover them. The template reconnect component keeps its isolated CSS for Blazor connection state transitions.

## Build and publish

```sh
dotnet build ISLA.sln -c Release
dotnet publish src/ISLA -c Release
```

Both commands generate minified CSS. Generated CSS, build outputs, and npm dependencies are ignored by source control. `SkipTailwind=true` is reserved for pipelines that have already generated `wwwroot/app.css`, such as the Docker build.

## Docker

```sh
docker compose up --build -d
```

Open http://localhost:8080. Stop with `docker compose down`.

Or build and run directly:

```sh
docker build -t isla .
docker run --rm -p 8080:8080 isla
```

The build uses `npm ci` and separate Node/.NET SDK stages. Only the published application and ASP.NET runtime enter the final image, which runs as the built-in non-root app user on port 8080.

For deployment, terminate TLS at your ingress/reverse proxy and enable WebSockets for interactive server components. Configure trusted forwarded headers for your hosting environment before HTTPS redirection if the proxy forwards requests over HTTP. Persist ASP.NET Core Data Protection keys outside the container when sessions must survive container replacement; multiple replicas also require an appropriate server interactivity/session affinity strategy. Supply environment-specific settings through environment variables or your deployment's secret store.

References: [Tailwind CLI](https://tailwindcss.com/docs/installation/tailwind-cli), [.NET container ports and users](https://learn.microsoft.com/dotnet/core/docker/introduction).

## Recipes and persistence

Open **Recipes** to add, search, edit, and delete recipes. Each recipe has a name, servings, ingredients, and optional method/notes. Ingredient names are reused case-insensitively across recipes; enter a new name or choose from the input suggestions. Saving a recipe adds its ingredients to the shared catalogue. Deleting a recipe keeps those names available.

Use whole-number quantities for individual items. Measurements support decimal values and UK metric/imperial cooking units: g, kg, ml, l, tsp, tbsp, oz, lb, fl oz, pint, and pinch. Measurements are stored as entered; automatic unit conversion is not performed.

Data is stored in `App_Data/recipes.json` by default, outside the static web root and ignored by Git. Override the directory with the `DataDirectory` environment variable. Writes are serialized and replace the file atomically. This lightweight file store supports a single running application instance shared by the household; simultaneous edits of the same recipe use the last saved version. Use a database before running multiple app instances against shared storage.

Docker Compose mounts the `recipe-data` named volume at `/app/data`. It survives container replacement and `docker compose down`; `docker compose down -v` removes it. Back up this volume (or the local data directory) to protect your collection. A direct `docker run` should also mount a volume with `-v recipe-data:/app/data`.

The app currently has no sign-in: everyone who can reach it shares the recipe collection. The opening page shows a Monday–Sunday calendar for the current UK week. Add one saved recipe per day for the evening meal. The same recipe can be planned on different days. Remove a planned meal using its trash icon to choose a replacement. Existing days with multiple meals remain visible so they can be removed without losing saved data. Navigate between weeks to plan ahead. Plans are persisted with the recipe collection.

The Shopping list page totals ingredients for the selected week. Each meal uses the full recipe quantities; matching names (case-insensitive) and units are added together. Kilograms convert to grams and litres to millilitres before combining; other units remain separate. Recipe edits update future shopping-list calculations; deleting a recipe also removes its planned meals. Existing recipe files are compatible and begin with an empty plan.

## Goods and extra shopping

Goods is the shared catalogue for food and non-food items, with no quantities. Existing ingredients migrate automatically to food goods. Saving a new recipe ingredient adds a food good. Only food goods appear in recipe suggestions, and saving a recipe with a known non-food good is rejected. A good used in recipes cannot be changed to non-food until removed from those recipes.

Add household goods such as toilet roll or bleach in **Goods** and choose **Non-food**. In **Shopping list**, select a good, quantity, and measurement under **Add extra shopping**. Extras are saved per week and included in the combined totals, alongside any recipe quantities for the same food. Edit quantities and remove items directly on the combined shopping-list rows. Goods categories can be changed in the catalogue, and names are unique ignoring case.

You can also select **Create new good** directly on the Shopping list, enter its name, choose Food or Non-food, and set a quantity and measurement. **Create and add to list** saves both the catalogue good and the selected week's extra item together. Existing names are rejected without changing their category; select them under **Existing good** instead.

Shopping-list rows have **Save** and **Remove** actions. These persist a final quantity (or hide the row) for that week without modifying recipes, meals, or the goods catalogue. Saved quantities remain fixed even if the plan changes. Adding an existing good as extra shopping clears that good/measurement override so it reappears with its new calculated total.

## Mobile layout

The primary navigation is a fixed bottom bar with icons, short labels, and an active-page highlight. Content reserves room for the bar and the device safe area. Controls have a minimum 44px touch height and inputs use 16px text. Shopping entries stack into cards on phones; forms, week navigation, and recipe cards adapt to wider screens.

The visual theme uses muted lavender, plum ink, and ivory colours with system serif headings and an original plum-and-ivory illustrated ISLA mascot. Theme tokens and reusable visual styles live in `Styles/app.css`; no external fonts or image services are required.

### ISLA identity

The interface and browser titles use ISLA — Indespensible Shopping List Application. The original mascot uses a restrained monochrome portrait in plum and ivory, combining classical editorial linework with a modern manga silhouette. The header uses `src/ISLA/wwwroot/images/isla-mascot.webp` (256px); `isla-mascot.png` is the full-resolution source, and `wwwroot/favicon.png` is the 64px browser icon. The muted lavender, plum, and ivory palette remains consistent across the app.

### Leftovers

Selecting a recipe already cooked earlier in the same Monday–Sunday week offers **Use leftovers** or **Cook fresh**. Fresh cooks add another full recipe to the shopping list; leftovers reference the earlier cook and add no ingredients. Earlier weeks and later days do not qualify. The planner labels leftover meals with their cooking day. If the original cook is removed, the first dependent leftover becomes a fresh cook and later leftovers use that meal, keeping ingredients accounted for.

### Check stock and Pantry

Enable **Check stock** on the Shopping list for a simplified view without quantity editing or extra-item forms. Mark items **In stock** to exclude them from the shopping list and show them in the compact **Pantry** section. Its plus buttons return items to the shopping list. Stock choices persist for the selected week, apply to the whole listed quantity, and distinguish measurements (e.g. item counts and grams). They do not track household inventory or carry automatically to other weeks.

### In-store mode and pricing

**In-store mode** shows a simple checklist, with purchased rows crossed out and still visible so they can be unticked. Enter an optional **total price for the full row** in pounds and pence before ticking it. Purchases and entered prices are saved immediately on the server. **Finish shopping** returns to normal view; checked items appear under **Purchased**, below **Pantry**, and can be returned to the shopping list.

The browser remembers in-store mode and its selected week in local storage. Revisiting the Shopping list resumes that mode and week unless a week is explicitly requested in the URL. Finishing shopping clears the saved mode. This preference is local to the browser; purchase records are shared household data. Browser storage must be enabled for the mode to survive reopening.

The summary shows an estimated total and how many entries have known prices. It includes purchased rows but excludes Pantry. Prices are remembered for each good and measurement, using the latest recorded total and quantity to scale future estimates (for example £2.40 for two peppers estimates £3.60 for three). Metric conversions happen before pricing, so grams/kilograms and millilitres/litres share a basis; other measurements remain distinct. An actual price entered for a purchase takes precedence over an estimate. Rows without a known price are omitted from the estimate and it is labelled partial. Unticking a purchase keeps its price history for future estimates.

## Verification

```sh
dotnet test ISLA.sln -c Release
node tests/browser/shopping-mode.test.mjs
```

The .NET suite covers purchase persistence, optional prices, price validation, future estimates, metric scaling, actual-price precedence, week isolation, and Pantry exclusion. The JavaScript suite checks mode persistence, clearing the preference on exit, and unavailable or malformed browser storage.

In-store price fields restore actual purchased prices or the same saved-price estimates used by the summary. Unchecked price drafts (including intentionally cleared fields) are saved immediately in the browser for that week and measurement and restored after reopening. Drafts do not affect household price history or the estimate until the item is marked purchased; successfully purchasing replaces the browser draft with the saved server price.

## Licence

ISLA is available under the [MIT licence](LICENSE).
