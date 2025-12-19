export default function RecetasPage() {
  return (
    <section className="space-y-4">
      <h1 className="text-3xl font-semibold">Recipes</h1>
      <p className="text-lg text-slate-200">
        Quick, repeatable ideas to iterate on the project. Each card can include ingredients,
        steps, and community notes.
      </p>
      <div className="grid gap-4 sm:grid-cols-2">
        {[1, 2, 3, 4].map((num) => (
          <article
            key={num}
            className="rounded-xl border border-white/10 bg-slate-900/70 p-4 shadow-sm"
          >
            <p className="text-sm uppercase tracking-wide text-cyan-200">Recipe #{num}</p>
            <p className="text-slate-200">Placeholder for titles and steps.</p>
          </article>
        ))}
      </div>
    </section>
  );
}
