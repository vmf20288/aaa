export default function PatrocinioPage() {
  return (
    <section className="space-y-4">
      <h1 className="text-3xl font-semibold">Patrocinio</h1>
      <p className="text-lg text-slate-200">
        Opciones para apoyar el proyecto y mantener la comunidad funcionando sin frenos.
      </p>
      <div className="rounded-2xl border border-white/10 bg-gradient-to-r from-slate-900 via-slate-800 to-slate-900 p-6 shadow-lg">
        <p className="font-semibold text-cyan-200">¿Cómo ayudar?</p>
        <ul className="list-disc space-y-2 pl-5 text-slate-200">
          <li>Donaciones puntuales o recurrentes.</li>
          <li>Colaboraciones en especie (equipo, diseño, logística).</li>
          <li>Voluntariado y mentoría.</li>
        </ul>
      </div>
    </section>
  );
}
