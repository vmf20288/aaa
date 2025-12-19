import Link from "next/link";

const cards = [
  {
    title: "Learn",
    description: "Basic and advanced concepts for anyone who wants to jump in full speed.",
    href: "/aprende",
  },
  {
    title: "Recipes",
    description: "Quick ideas to implement with the community and keep accelerating.",
    href: "/recetas",
  },
  {
    title: "Community",
    description: "Stories, events, and testimonials from people living No Engine Brake.",
    href: "/comunidad",
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
              A fast starting point to share resources, ideas, and community around the No
              Engine Brake project.
            </p>
          </div>
          <div className="rounded-xl bg-white/10 px-6 py-4 text-sm text-cyan-50 shadow-md">
            <p className="font-semibold">What&apos;s inside?</p>
            <ul className="list-disc space-y-1 pl-5 text-slate-100">
              <li>Simple navigation</li>
              <li>Sample content</li>
              <li>Ready to iterate</li>
            </ul>
          </div>
        </div>
      </section>

      <section className="grid gap-6 sm:grid-cols-2">
        {cards.map((card) => (
          <Link
            key={card.title}
            href={card.href}
            className="group rounded-2xl border border-white/10 bg-white/5 p-6 shadow-lg transition hover:-translate-y-1 hover:border-cyan-200/50 hover:shadow-cyan-500/20"
          >
            <div className="flex items-center justify-between">
              <h2 className="text-xl font-semibold text-white">{card.title}</h2>
              <span className="text-sm text-cyan-200 transition group-hover:translate-x-1">
                Explore →
              </span>
            </div>
            <p className="mt-3 text-slate-200">{card.description}</p>
          </Link>
        ))}
      </section>
    </div>
  );
}
