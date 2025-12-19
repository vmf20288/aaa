export default function RecetasPage() {
  return (
    <section className="space-y-4">
      <h1 className="text-3xl font-semibold">Recetas</h1>
      <p className="text-lg text-slate-200">
        Ideas rápidas y replicables para iterar sobre el proyecto. Cada tarjeta puede
dejar espacio para ingredientes, pasos y notas de la comunidad.
      </p>
      <div className="grid gap-4 sm:grid-cols-2">
        {[1, 2, 3, 4].map((num) => (
          <article
            key={num}
            className="rounded-xl border border-white/10 bg-slate-900/70 p-4 shadow-sm"
          >
            <p className="text-sm uppercase tracking-wide text-cyan-200">Receta #{num}</p>
            <p className="text-slate-200">Espacio reservado para títulos y pasos.</p>
          </article>
        ))}
      </div>
    </section>
  );
}
