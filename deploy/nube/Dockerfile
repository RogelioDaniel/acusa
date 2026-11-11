# Acusa Central para la nube (Linux). Se construye desde la raíz del repositorio:
#   docker build -f deploy/nube/Dockerfile -t acusa-central .
# Solo incluye Central y el contrato; el worker NO va aquí: corre en el servidor Windows de Comercial.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS compilar
WORKDIR /src
COPY src/Acusa.Contrato/ src/Acusa.Contrato/
COPY src/Acusa.Orquestador/ src/Acusa.Orquestador/
RUN dotnet publish src/Acusa.Orquestador/Acusa.Orquestador.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=compilar /app .
# Los datos (SQLite) van en un volumen persistente montado en /datos; sin él se pierden al reiniciar.
ENV Urls=http://0.0.0.0:8080 \
    Orquestador__BaseDatos=/datos/orquestador.db \
    Orquestador__ProxyInverso=true
EXPOSE 8080
VOLUME /datos
ENTRYPOINT ["dotnet", "Acusa.Orquestador.dll"]
