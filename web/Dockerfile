FROM node:20-alpine AS build
WORKDIR /src
COPY package.json package-lock.json* ./
RUN npm install --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/web/browser /usr/share/nginx/html
EXPOSE 8080
