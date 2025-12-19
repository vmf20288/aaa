export default function ComunidadPage() {
  return (
    <section className="space-y-6">
      <h1 className="text-3xl font-semibold">Comunidad pronto</h1>
      <p className="text-lg text-slate-200">
        Estamos afinando los detalles antes de abrir el espacio comunitario. Mientras tanto,
        puedes sumarte a la lista de espera o escribirnos cualquier pregunta.
      </p>
      <div className="flex flex-col gap-3 sm:flex-row">
        <a
          href="mailto:noenginebrake3@gmail.com?subject=Quiero%20unirme%20a%20No%20Engine%20Brake"
          className="inline-flex items-center justify-center rounded-lg bg-cyan-400 px-4 py-2 font-semibold text-slate-900 shadow transition hover:bg-cyan-300"
        >
          Unirme a la lista de espera
        </a>
        <a
          href="mailto:noenginebrake3@gmail.com?subject=Pregunta%20sobre%20alimentacion%20en%20camion"
          className="inline-flex items-center justify-center rounded-lg border border-white/20 px-4 py-2 font-semibold text-cyan-100 transition hover:border-cyan-300 hover:text-white"
        >
          Enviar una pregunta
        </a>
      </div>
      <div className="rounded-xl border border-white/10 bg-white/5 p-4 shadow">
        <p className="font-medium text-cyan-200">Pronto abriremos el canal principal</p>
        <p className="text-slate-200">
          No hay Discord todavía, pero te avisaremos en cuanto esté listo para que podamos
          conectar y compartir recursos.
        </p>
      </div>
    </section>
  );
}
