# Build
FROM node:22-alpine AS build
WORKDIR /app
COPY src/frontend/app/package.json src/frontend/app/package-lock.json* ./
RUN npm install
COPY src/frontend/app/ .
RUN npm run build

# Production
FROM nginx:alpine AS final
COPY --from=build /app/dist /usr/share/nginx/html
COPY src/frontend/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
