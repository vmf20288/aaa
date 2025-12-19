export default function ComunidadPage() {
  return (
    <section className="space-y-4">
      <h1 className="text-3xl font-semibold">Comunidad</h1>
      <p className="text-lg text-slate-200">
        Historias, testimonios y convocatorias para conectar con más personas que viven
        el espíritu No Engine Brake.
      </p>
      <div className="space-y-3">
        {["Próximo meetup", "Historias destacadas", "Canales y redes"].map((item) => (
          <div key={item} className="rounded-xl border border-white/10 bg-white/5 p-4 shadow">
            <p className="font-medium text-cyan-200">{item}</p>
            <p className="text-slate-200">Contenido pendiente de completar.</p>
          </div>
        ))}
      </div>
    </section>
  );
}
