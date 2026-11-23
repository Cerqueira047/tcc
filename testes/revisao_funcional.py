"""Revisão funcional em um banco descartável criado exclusivamente para este teste."""
import json
import os
import subprocess
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

BASE = "http://127.0.0.1:5241"
DB = "nexora_revisao"
NOW = datetime.now(timezone.utc)
results = []
responses = []

def http(path, method="GET", data=None, expect=200):
    req = Request(BASE + path, method=method, data=json.dumps(data).encode() if data is not None else None,
                  headers={"Content-Type": "application/json"})
    try:
        response = urlopen(req, timeout=15)
    except HTTPError as error:
        response = error
    with response:
        status = response.status
        body = response.read().decode()
    responses.append((method, path, status))
    assert status == expect, f"{method} {path}: esperado {expect}, recebido {status}, {body[:180]}"
    return json.loads(body) if body else None

def sql(query):
    return subprocess.check_output(["psql", "-XAt", "-v", "ON_ERROR_STOP=1", "-d", DB, "-c", query], text=True).strip()

def check(name, action):
    try:
        detail = action()
        row = ("OK", name, str(detail or ""))
    except Exception as error:
        row = ("FALHA", name, f"{type(error).__name__}: {error}")
    results.append(row)
    print(" | ".join(row), flush=True)

def equal(actual, expected):
    assert actual == expected, f"obtido {actual!r}, esperado {expected!r}"

def stamp(seconds):
    return (NOW + timedelta(seconds=seconds)).isoformat()

def event(key, source, amount, quantity, ts=-60, kind="PedidoAtualizado", suffix=""):
    return {"recordId":key, "systemName":source, "eventId":key+source+suffix, "eventType":kind,
            "occurredAt":stamp(ts), "data":{"amount":amount, "quantity":quantity}}

def post(item):
    return http("/api/events/", "POST", item, 201)

def alerts(flag=None):
    return http("/api/alerts?pageSize=200"+("" if flag is None else "&resolved="+str(flag).lower()))["data"]

def summary():
    s = http("/api/dashboard/summary")
    raw = sql('''SELECT (SELECT count(*) FROM "Events"), count(*),
         count(*) FILTER (WHERE NOT "Resolved"), count(*) FILTER (WHERE "Resolved"),
         coalesce(sum("FinancialDifference") FILTER (WHERE NOT "Resolved"),0),
         (SELECT count(*) FROM (SELECT 1 FROM "Alerts" GROUP BY "RecordId","EventType" HAVING count(*)>1) g)
         FROM "Alerts"''').split("|")
    keys = ["totalEventos","totalAlertas","alertasPendentes","alertasResolvidos","impactoFinanceiro","totalReincidencias"]
    equal([s[k] for k in keys], list(map(float,raw)))
    return s

def pair(key, diff, delta, level, fields):
    one = post(event(key,"SistemaVendas",100,2))
    equal(one["inconsistenciasDetectadas"],[])
    payload = event(key,"SistemaEstoque",100+diff,2+delta,-45)
    two = post(payload)
    row = two["evento"]
    equal(http("/api/events/"+row["id"])["id"],row["id"])
    saved = sql('''SELECT row_to_json(e) FROM "Events" e WHERE "Id"='%s' ''' % row["id"])
    persisted = json.loads(saved)
    equal([persisted["SystemName"],persisted["RecordId"],json.loads(persisted["Data"])],
          [payload["systemName"],key,payload["data"]])
    found = [a for a in alerts() if a["recordId"]==key]
    if not fields:
        equal(two["inconsistenciasDetectadas"],[]); equal(found,[])
        return "0 anomalias e 0 alertas"
    finding, = two["inconsistenciasDetectadas"]
    equal(sorted(o["campo"] for o in finding["ocorrenciasPedidos"]),sorted(fields))
    equal(finding["diferencaFinanceira"],diff)
    a, = found
    equal((a["severity"],a["financialDifference"]),(level,diff))
    for field, amount in [("valorTotal",diff),("quantidadeTotal",delta)]:
        if field in fields:
            label = "valor financeiro" if field=="valorTotal" else "quantidade"
            assert f"Divergência de {label}: diferença de {amount}" in a["message"], a["message"]
    return f"{len(fields)} ocorrência(s), {len(found)} alerta(s), {level}. "+a["message"]

def by_key(key):
    return next(a for a in alerts() if a["recordId"]==key)

def open_cases():
    wanted = {a["registro"] for a in http("/api/events/anomalies")}
    equal(wanted, {"F100","F500","Q","DOIS"})
    equal(len(http("/api/alerts/prioritized")),4)
    equal(http("/api/alerts/prioritized")[0]["recordId"],"F500")
    equal(http("/api/alerts/recurrences"),[])
    equal(set(http("/api/systems")["sistemasObservados"]),{"SistemaVendas","SistemaEstoque"})
    equal(http("/api/events?page=2&pageSize=3")["total"],10)

def two_alerts():
    observed = len([a for a in alerts() if a["recordId"]=="DOIS"])
    equal(observed,2)

def replay():
    item = event("F100","SistemaEstoque",200,2,-45)
    before = summary()
    http("/api/events/", "POST",item,409)
    http("/api/events/anomalies"); http("/api/events/anomalies")
    equal(summary(),before)
    a=by_key("F100"); equal(len(http("/api/alerts/"+a["id"]+"/history")),1)
    return "Reenvio HTTP 409; duas consultas mantiveram as contagens."

def parse_time(value):
    return datetime.fromisoformat(value.replace("Z","+00:00"))

def resolve():
    a=by_key("F100"); aid=a["id"]; before=summary()
    assert NOW - timedelta(seconds=2) <= parse_time(a["createdAt"]) <= datetime.now(timezone.utc)
    http("/api/alerts/"+aid+"/resolve","PATCH")
    http("/api/alerts/"+aid+"/resolve","PATCH",expect=409)
    assert aid not in {x["id"] for x in alerts(False)}
    assert aid in {x["id"] for x in alerts(True)}
    h=http("/api/alerts/"+aid+"/history")
    equal([x["action"] for x in h],["Criado","Resolvido"])
    dates=[parse_time(x["createdAt"]) for x in h]
    assert NOW-timedelta(seconds=2)<=dates[0]<=dates[1]<=datetime.now(timezone.utc)
    equal(sql('''SELECT string_agg("Action",',' ORDER BY "CreatedAt") FROM "AlertHistories" WHERE "AlertId"='%s' ''' % aid),"Criado,Resolvido")
    after=summary()
    equal(after,dict(before,alertasPendentes=before["alertasPendentes"]-1,alertasResolvidos=before["alertasResolvidos"]+1,impactoFinanceiro=before["impactoFinanceiro"]-100))
    return json.dumps(after,ensure_ascii=False)

def monitor():
    # 20 chaves distintas por sistema: 0/20, 1/20 e 3/20 = 0%, 5% e 15%.
    for name,n in [("Zero",0),("Cinco",1),("Quinze",3)]:
        for i in range(20):
            key=name+str(i); post(event(key,name,10,1,kind="Leitura"))
            if i<n: post(event(key,"Referencia",11,1,kind="Leitura"))
    m={v["sistema"]:v for v in http("/api/systems/performance")}
    for name,rate,label in [("Zero",0,"Estável"),("Cinco",5,"Atenção"),("Quinze",15,"Crítico")]:
        equal((m[name]["percentualAlertas"],m[name]["classificacao"]),(rate,label))
    health=http("/api/systems/health")
    equal(health["totalSistemas"],len(m))
    for field,label in [("sistemasEstaveis","Estável"),("sistemasEmAtencao","Atenção"),("sistemasCriticos","Crítico")]:
        equal(health[field],sum(x["classificacao"]==label for x in m.values()))
    return "0% estável; 5% atenção; 15% crítico. Resumo confere com a lista."

def recurrence():
    previous=summary()
    post(event("F100","SistemaEstoque",200,2,-10,suffix="-novo"))
    rec, = http("/api/alerts/recurrences")
    equal((rec["recordId"],rec["totalOcorrencias"],rec["ocorrenciasPendentes"]),("F100",2,1))
    equal(summary()["totalReincidencias"],previous["totalReincidencias"]+1)

def errors():
    missing="00000000-0000-0000-0000-000000000000"
    http("/api/events/"+missing,expect=404)
    http("/api/alerts/"+missing+"/resolve","PATCH",expect=404)
    http("/api/alerts/"+missing+"/history",expect=404)
    http("/api/events/","POST",{},400)
    http("/api/events/","POST",event("futuro","X",10,1,3600),400)

def screen():
    from selenium import webdriver
    from selenium.webdriver.common.by import By
    from selenium.webdriver.support.ui import WebDriverWait
    opts=webdriver.ChromeOptions(); opts.add_argument("--headless=new"); opts.add_argument("--no-sandbox")
    opts.add_argument("--disable-dev-shm-usage"); opts.add_argument("--window-size=1600,1200")
    with webdriver.Chrome(options=opts) as browser:
        wait=WebDriverWait(browser,45)
        browser.get("http://127.0.0.1:5173")
        def same(expected):
            def ready(driver):
                cards=driver.find_elements(By.CSS_SELECTOR,".stats-grid .stat-card strong")
                target=[str(expected["totalEventos"]),str(expected["alertasPendentes"]),str(expected["alertasResolvidos"])]
                if len(cards)!=4 or [x.text for x in cards[:3]]!=target: return False
                currency=cards[3].text.replace("R$","").replace(".","").replace(",",".").strip()
                return float(currency)==expected["impactoFinanceiro"]
            wait.until(ready)
        same(summary())
        pair("TELA",125,0,"Média",["valorTotal"])
        same(summary())  # Atualização automática de 30 s, sem recarregar a página.
        row=wait.until(lambda d:next((tr for tr in d.find_elements(By.CSS_SELECTOR,"table tbody tr") if "TELA" in tr.text),None))
        row.find_element(By.TAG_NAME,"button").click(); wait.until(lambda d:d.switch_to.alert).accept()
        wait.until(lambda d:by_key("TELA")["resolved"])
        same(summary())
        return "Quatro cartões comparados ao SQL. Atualização automática e resolução pela interface confirmadas."

def offline():
    subprocess.run(["sudo","systemctl","stop","postgresql"],check=True)
    r=http("/api/status",expect=503)
    equal(r["banco"],"Indisponível")

def initial():
    equal(http("/")["status"],"Online");equal(http("/api/status")["banco"],"Conectado")
    equal(sum(summary().values()),0)

check("API online e painel com banco vazio",initial)
check("Pedido correto",lambda:pair("NORMAL",0,0,None,[]))
check("Divergência financeira no limite médio",lambda:pair("F100",100,0,"Média",["valorTotal"]))
check("Divergência financeira no limite alto",lambda:pair("F500",500,0,"Alta",["valorTotal"]))
check("Somente quantidade",lambda:pair("Q",0,2,"Baixa",["quantidadeTotal"]))
check("Valor e quantidade juntos: conteúdo",lambda:pair("DOIS",25,1,"Baixa",["valorTotal","quantidadeTotal"]))
check("Um alerta individual por inconsistência simultânea",two_alerts)
check("Anomalias, listagens, prioridade, sistemas e paginação",open_cases)
check("Dashboard após novos eventos / SQL",summary)
check("Reenvio e releitura sem duplicatas",replay)
check("Resolução, filtros, histórico, datas UTC e dashboard / SQL",resolve)
check("Três classificações de monitoramento",monitor)
check("Reincidência após resolução",recurrence)
check("Respostas esperadas para dados inválidos e IDs ausentes",errors)
check("Dashboard real no Chrome, atualização e resolução",screen)
check("Dashboard final / SQL",summary)
check("HTTP 500 nas rotas funcionais exercitadas",lambda:equal([r for r in responses if r[2]>=500],[]))
check("Status com PostgreSQL indisponível",offline)
with open(os.environ["GITHUB_STEP_SUMMARY"],"a") as f:
    f.write("## Revisão do NEXORA\n\n| Resultado | Verificação | Evidência |\n|---|---|---|\n")
    for state,name,detail in results:
        f.write(f"| {state} | {name} | {detail.replace('|','/').replace(chr(10),' ')[:450]} |\n")
    f.write(f"\n{len(responses)} requisições HTTP instrumentadas pelo teste Python.\n")
raise SystemExit(any(row[0]=="FALHA" for row in results))
