using WinDock.Services;

// Exercita o BrightnessService real, sem a dock no meio, e NA MESMA THREAD que a dock usa.
//
// Existe porque o serviço funciona aqui e falha dentro da barra — com o mesmo código, o mesmo
// manifesto (PerMonitorV2 + requireAdministrator) e os mesmos monitores. Já foram descartados,
// por medição: elevação, consciência de DPI, publicação em arquivo único e a escolha do monitor
// (a barra varre os dois e ambos recusam). Sobrou o apartamento COM da thread: a barra chama
// isto da thread da interface do WPF, que é STA; um Main de console é MTA.
//
// O argumento "sta" roda a mesma sequência numa thread STA.

var sta = args.Contains("sta");
Console.WriteLine(sta ? "modo: thread STA (como a barra)" : "modo: thread MTA (como um console)");
Console.WriteLine();

var codigo = 0;
var t = new Thread(() => codigo = Rodar());
t.SetApartmentState(sta ? ApartmentState.STA : ApartmentState.MTA);
t.Start();
t.Join();
return codigo;

static int Rodar()
{
    var servico = new BrightnessService();

    var nivel = servico.Level;
    Console.WriteLine($"   nível = {nivel}");

    var disponivel = servico.Available;
    Console.WriteLine($"   disponível = {disponivel}");

    if (disponivel)
    {
        var antes = servico.Level;
        servico.Set(Math.Min(100, antes + 1));
        Thread.Sleep(400);
        Console.WriteLine($"   depois de Set: nível = {servico.Level}");
        servico.Set(antes);
        Thread.Sleep(400);
        Console.WriteLine($"   devolvido: nível = {servico.Level}");
    }

    servico.Dispose();
    Console.WriteLine();
    Console.WriteLine(disponivel ? "resultado: funciona" : "resultado: REPRODUZIDO — é o apartamento da thread");
    return disponivel ? 0 : 1;
}
