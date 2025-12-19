const cards = [
  {
    title: "Aprende",
    description: "Conceptos básicos y avanzados para quienes quieren sumarse sin frenos.",
    href: "/aprende",
  },
  {
    title: "Recetas",
    description: "Ideas rápidas para implementar en comunidad y seguir acelerando.",
    href: "/recetas",
  },
  {
    title: "Comunidad",
    description: "Historias, eventos y testimonios de quienes viven No Engine Brake.",
    href: "/comunidad",
  },
  {
    title: "Patrocinio",
    description: "Opciones para apoyar el proyecto y mantenerlo en marcha.",
    href: "/patrocinio",
  },
];

export default function HomePage() {
  return (
    <div className="space-y-10">
      <section className="rounded-2xl bg-gradient-to-r from-indigo-500 via-blue-500 to-cyan-400 p-[1px] shadow-xl">
        <div className="flex flex-col gap-6 rounded-2xl bg-slate-950/80 p-10 sm:flex-row sm:items-center sm:justify-between">
          <div className="space-y-2">
            <p className="text-sm uppercase tracking-[0.3em] text-cyan-200">MVP</p>
            <h1 className="text-4xl font-semibold tracking-tight sm:text-5xl">No Engine Brake</h1>
            <p className="max-w-2xl text-lg text-slate-200">
              Un punto de partida rápido para compartir recursos, ideas y comunidad alrededor
              del proyecto No Engine Brake.
            </p>
          </div>
          <div className="rounded-xl bg-white/10 px-6 py-4 text-sm text-cyan-50 shadow-md">
            <p className="font-semibold">¿Qué hay aquí?</p>
            <ul className="list-disc space-y-1 pl-5 text-slate-100">
              <li>Navegación simple</li>
              <li>Contenido de ejemplo</li>
              <li>Listo para iterar</li>
            </ul>
          </div>
        </div>
      </section>

      <section className="grid gap-6 sm:grid-cols-2">
        {cards.map((card) => (
          <a
            key={card.title}
            href={card.href}
            className="group rounded-2xl border border-white/10 bg-white/5 p-6 shadow-lg transition hover:-translate-y-1 hover:border-cyan-200/50 hover:shadow-cyan-500/20"
          >
            <div className="flex items-center justify-between">
              <h2 className="text-xl font-semibold text-white">{card.title}</h2>
              <span className="text-sm text-cyan-200 transition group-hover:translate-x-1">
                Explorar →
              </span>
            </div>
            <p className="mt-3 text-slate-200">{card.description}</p>
          </a>
        ))}
      </section>
    </div>
  );
}
